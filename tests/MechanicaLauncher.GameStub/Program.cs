Console.WriteLine("[Render thread/INFO]: Minecraft fixture started");
Console.WriteLine("--accessToken fixture-secret-token-0123456789");
await Task.Delay(args.Contains("--fixture-wait") ? 15_000 : 700);
if (args.Contains("--fixture-crash"))
{
    Console.Error.WriteLine("java.lang.OutOfMemoryError: Java heap space");
    return 1;
}
Console.WriteLine("[Render thread/INFO]: Stopping!");
return 0;
