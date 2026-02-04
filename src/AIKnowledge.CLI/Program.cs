if (args.Length == 0)
{
    Console.WriteLine("AIKnowledge Platform CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: aikp <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  ingest <path>            Add file/folder to knowledge base");
    Console.WriteLine("  search \"<query>\"          Search knowledge base");
    Console.WriteLine("  chat                     Interactive agent session");
    Console.WriteLine("  serve                    Start local web server");
    Console.WriteLine("  config set <key> <value> Update settings");
    return;
}

Console.WriteLine($"Command '{args[0]}' is not yet implemented.");
