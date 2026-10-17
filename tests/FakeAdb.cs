using System;
using System.Text;
using System.Threading;
public static class FakeAdb
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "sleep") { Thread.Sleep(30000); return 0; }
        foreach (string value in args) Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
        return 0;
    }
}
