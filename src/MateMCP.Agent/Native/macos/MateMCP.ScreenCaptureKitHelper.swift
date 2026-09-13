import Foundation
import CoreGraphics
import ScreenCaptureKit
import CoreMedia
import VideoToolbox
import ImageIO
import UniformTypeIdentifiers

final class FrameOutput: NSObject, SCStreamOutput, SCStreamDelegate {
    private let output = FileHandle.standardOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .screen, sampleBuffer.isValid, let pixelBuffer = sampleBuffer.imageBuffer else { return }

        var source: CGImage?
        let status = VTCreateCGImageFromCVPixelBuffer(pixelBuffer, options: nil, imageOut: &source)
        guard status == noErr, let source else { return }

        // Detach from ScreenCaptureKit's IOSurface before encoding. Encoding a CGImage
        // that still references the stream-owned pixel buffer can block the sample queue.
        let width = source.width
        let height = source.height
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return }
        context.draw(source, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let owned = context.makeImage() else { return }

        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { return }
        let options = [kCGImageDestinationLossyCompressionQuality: 0.72] as CFDictionary
        CGImageDestinationAddImage(destination, owned, options)
        guard CGImageDestinationFinalize(destination) else { return }

        let payload = data as Data
        guard !payload.isEmpty, payload.count <= 16 * 1024 * 1024 else { return }
        var header = Data()
        for value in [UInt32(payload.count), UInt32(width), UInt32(height)] {
            var bigEndian = value.bigEndian
            withUnsafeBytes(of: &bigEndian) { header.append(contentsOf: $0) }
        }
        output.write(header)
        output.write(payload)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        FileHandle.standardError.write(Data("ScreenCaptureKit stream stopped: \(error)\n".utf8))
        exit(4)
    }
}

@main
struct MateMCPScreenCaptureKitHelper {
    static func main() async {
        _ = CGMainDisplayID()
        guard CommandLine.arguments.count >= 2, let windowID = UInt32(CommandLine.arguments[1]) else {
            FileHandle.standardError.write(Data("usage: MateMCP.ScreenCaptureKitHelper <window-id> [fps]\n".utf8))
            exit(2)
        }
        let fps = max(1, min(Int(CommandLine.arguments.count >= 3 ? CommandLine.arguments[2] : "4") ?? 4, 12))

        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            guard let window = content.windows.first(where: { $0.windowID == windowID }) else {
                FileHandle.standardError.write(Data("window not found: \(windowID)\n".utf8))
                exit(3)
            }

            let filter = SCContentFilter(desktopIndependentWindow: window)
            let configuration = SCStreamConfiguration()
            let targetWidth = min(1000.0, max(480.0, window.frame.width * 1.25))
            configuration.width = max(1, Int(targetWidth))
            configuration.height = max(1, Int(targetWidth * window.frame.height / max(window.frame.width, 1.0)))
            configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
            configuration.queueDepth = 3
            configuration.showsCursor = false
            configuration.capturesAudio = false

            let receiver = FrameOutput()
            let stream = SCStream(filter: filter, configuration: configuration, delegate: receiver)
            try stream.addStreamOutput(receiver, type: .screen, sampleHandlerQueue: DispatchQueue(label: "com.matemcp.preview.frames", qos: .userInteractive))
            try await stream.startCapture()

            // The parent Agent keeps stdin open. If it exits or replaces this session,
            // EOF closes the helper so update/restart cannot leave orphan capture tasks.
            DispatchQueue.global(qos: .utility).async {
                while true {
                    if FileHandle.standardInput.availableData.isEmpty { exit(0) }
                }
            }

            while true { try await Task.sleep(nanoseconds: 60_000_000_000) }
        } catch {
            FileHandle.standardError.write(Data("ScreenCaptureKit start failed: \(error)\n".utf8))
            exit(5)
        }
    }
}
