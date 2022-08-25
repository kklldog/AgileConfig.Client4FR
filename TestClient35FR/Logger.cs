using AgileConfig.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestClient4FR
{
    class MyLogger : ILogger
    {
        public void LogError(string msg)
        {
            Console.WriteLine("ERROR:" + msg);
        }

        public void LogError(Exception ex, string msg)
        {
            Console.WriteLine("ERROR:" + ex);
            Console.WriteLine("ERROR:" + msg);
        }

        public void LogTrace(string msg)
        {
            Console.WriteLine("TRACE:" + msg);
        }
    }
}
