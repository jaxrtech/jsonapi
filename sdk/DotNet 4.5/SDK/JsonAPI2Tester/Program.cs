using System;
using System.Threading.Tasks;
using JsonApi2;
using Nito.AsyncEx;

namespace JsonAPI2Tester
{
    class Program
    {
        static async Task<int> MainAsync()
        {
            var j = new JsonApi("localhost", 20059, "ftweb", "private");
            await j.ConnectAsync();

            j.StreamDataReceived += (sender, eventArgs) => Console.Write(eventArgs.Data["line"]);
            j.Subscribe("console");

            while (true)
            {
                var line = Console.ReadLine();
                await j.CallAsync("runConsoleCommand", null, line);
            }
        }

        static int Main(string[] args)
        {
            return AsyncContext.Run(new Func<Task<int>>(MainAsync));
        }
    }
}
