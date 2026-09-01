Console.WriteLine("Password:");
Console.Out.Flush();
var password = Console.ReadLine();
if (password == "mate-test-secret-12345")
{
    Console.WriteLine("AUTHENTICATED");
}
else
{
    Console.WriteLine("DENIED");
}
Console.Out.Flush();
