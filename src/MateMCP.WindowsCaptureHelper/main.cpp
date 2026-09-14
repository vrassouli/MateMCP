#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <windows.graphics.capture.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Storage.Streams.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <fcntl.h>
#include <iostream>
#include <io.h>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

using namespace std::chrono_literals;
using winrt::Windows::Foundation::Metadata::ApiInformation;
using winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool;
using winrt::Windows::Graphics::Capture::GraphicsCaptureItem;
using winrt::Windows::Graphics::Capture::GraphicsCaptureSession;
using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
using winrt::Windows::Graphics::Imaging::BitmapAlphaMode;
using winrt::Windows::Graphics::Imaging::BitmapEncoder;
using winrt::Windows::Graphics::Imaging::SoftwareBitmap;
using winrt::Windows::Storage::Streams::DataReader;
using winrt::Windows::Storage::Streams::InMemoryRandomAccessStream;

namespace
{
    constexpr uint32_t D3D11SdkVersion = 7;
    constexpr uint32_t BgraSupport = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

    GraphicsCaptureItem create_item(HWND hwnd)
    {
        auto factory = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{ nullptr };
        winrt::check_hresult(factory->CreateForWindow(
            hwnd,
            winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
            reinterpret_cast<void**>(winrt::put_abi(item))));
        return item;
    }

    IDirect3DDevice create_device()
    {
        winrt::com_ptr<ID3D11Device> d3d;
        winrt::com_ptr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL level{};
        winrt::check_hresult(D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            BgraSupport,
            nullptr,
            0,
            D3D11SdkVersion,
            d3d.put(),
            &level,
            context.put()));

        auto dxgi = d3d.as<IDXGIDevice>();
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        return inspectable.as<IDirect3DDevice>();
    }

    void write_u32_be(uint32_t value)
    {
        const unsigned char bytes[4]{
            static_cast<unsigned char>((value >> 24) & 0xff),
            static_cast<unsigned char>((value >> 16) & 0xff),
            static_cast<unsigned char>((value >> 8) & 0xff),
            static_cast<unsigned char>(value & 0xff)
        };
        std::cout.write(reinterpret_cast<char const*>(bytes), sizeof(bytes));
    }

    std::vector<uint8_t> encode_jpeg(winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DSurface const& surface)
    {
        auto bitmap = SoftwareBitmap::CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode::Ignore).get();
        InMemoryRandomAccessStream stream;
        auto encoder = BitmapEncoder::CreateAsync(BitmapEncoder::JpegEncoderId(), stream).get();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().get();

        const auto size = stream.Size();
        if (size == 0 || size > 16ull * 1024ull * 1024ull)
            throw std::runtime_error("Windows Graphics Capture emitted an invalid JPEG size.");

        stream.Seek(0);
        DataReader reader(stream.GetInputStreamAt(0));
        reader.LoadAsync(static_cast<uint32_t>(size)).get();
        std::vector<uint8_t> bytes(static_cast<size_t>(size));
        reader.ReadBytes(winrt::array_view<uint8_t>(bytes));
        return bytes;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2 || argc > 3)
    {
        std::wcerr << L"Usage: MateMCP.WindowsCaptureHelper <window-hwnd-hex> [fps]\n";
        return 2;
    }

    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        _setmode(_fileno(stdout), _O_BINARY);

        const auto raw = std::stoull(argv[1], nullptr, 16);
        const auto hwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(raw));
        if (!IsWindow(hwnd)) throw std::runtime_error("The target window is no longer available.");

        int fps = 4;
        if (argc == 3) fps = std::clamp(std::stoi(argv[2]), 1, 15);
        const auto minInterval = std::chrono::milliseconds(1000 / fps);

        auto item = create_item(hwnd);
        auto device = create_device();
        auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0) throw std::runtime_error("The target window has an invalid capture size.");

        auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
            device,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            size);
        auto session = pool.CreateCaptureSession(item);
        if (ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsCursorCaptureEnabled"))
        {
            try { session.IsCursorCaptureEnabled(false); }
            catch (...) { }
        }

        std::atomic_flag busy = ATOMIC_FLAG_INIT;
        std::mutex outputMutex;
        std::atomic<int64_t> lastEncodedMs{ 0 };
        const auto epoch = std::chrono::steady_clock::now();

        auto frameToken = pool.FrameArrived([&](Direct3D11CaptureFramePool const& sender, winrt::Windows::Foundation::IInspectable const&)
        {
            if (busy.test_and_set(std::memory_order_acquire)) return;
            try
            {
                auto frame = sender.TryGetNextFrame();
                if (!frame) { busy.clear(std::memory_order_release); return; }

                const auto content = frame.ContentSize();
                if (content.Width <= 0 || content.Height <= 0)
                {
                    busy.clear(std::memory_order_release);
                    return;
                }

                if (content.Width != size.Width || content.Height != size.Height)
                {
                    frame.Close();
                    size = content;
                    sender.Recreate(device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
                    busy.clear(std::memory_order_release);
                    return;
                }

                const auto nowMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - epoch).count();
                const auto previous = lastEncodedMs.load(std::memory_order_relaxed);
                if (previous != 0 && nowMs - previous < minInterval.count())
                {
                    busy.clear(std::memory_order_release);
                    return;
                }
                lastEncodedMs.store(nowMs, std::memory_order_relaxed);

                auto bytes = encode_jpeg(frame.Surface());
                std::lock_guard lock(outputMutex);
                write_u32_be(static_cast<uint32_t>(bytes.size()));
                write_u32_be(static_cast<uint32_t>(content.Width));
                write_u32_be(static_cast<uint32_t>(content.Height));
                std::cout.write(reinterpret_cast<char const*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
                std::cout.flush();
            }
            catch (winrt::hresult_error const& ex)
            {
                std::wcerr << L"Windows Graphics Capture frame error: " << ex.message().c_str() << L"\n";
            }
            catch (std::exception const& ex)
            {
                std::cerr << "Windows Graphics Capture frame error: " << ex.what() << "\n";
            }
            busy.clear(std::memory_order_release);
        });

        auto closedToken = item.Closed([&](GraphicsCaptureItem const&, winrt::Windows::Foundation::IInspectable const&)
        {
            std::cerr << "Windows Graphics Capture target closed.\n";
        });

        session.StartCapture();

        // Agent owns this helper's stdin pipe. Closing it is the graceful stop signal.
        char ignored{};
        while (std::cin.get(ignored)) { }

        item.Closed(closedToken);
        pool.FrameArrived(frameToken);
        session.Close();
        pool.Close();
        return 0;
    }
    catch (winrt::hresult_error const& ex)
    {
        std::wcerr << L"Windows Graphics Capture failed: " << ex.message().c_str() << L" (0x" << std::hex << static_cast<uint32_t>(ex.code().value) << L")\n";
        return 1;
    }
    catch (std::exception const& ex)
    {
        std::cerr << "Windows Graphics Capture failed: " << ex.what() << "\n";
        return 1;
    }
}
