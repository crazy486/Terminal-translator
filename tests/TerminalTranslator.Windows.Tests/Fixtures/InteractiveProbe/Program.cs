using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.Write("\r\ninterrupt handled\r\nprobe> ");
};

string scenario = args.FirstOrDefault() ?? "echo";
switch (scenario)
{
    case "frames":
        Console.Write("ten percent\rcomplete\r\n");
        Console.Write("\u001b[?1049hhidden alternate frame\u001b[?1049l");
        Console.Write("final visible frame\r\n");
        return 17;
    case "prompt":
        Console.Write("Continue with the operation? ");
        string? answer = Console.ReadLine();
        Console.WriteLine($"answer: {answer}");
        return 0;
    default:
        Console.Write("probe> ");
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (line.StartsWith("exit ", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(5), out int exitCode))
            {
                return exitCode;
            }

            Console.WriteLine($"echo: {line}");
            Console.Write("probe> ");
        }

        return 0;
}
