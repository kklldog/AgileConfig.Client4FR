﻿using AgileConfig.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using TestClient4FR;

namespace TestClient35FR
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");

            var appid = "test_app";
            var secret = "test_app";
            var servers = "http://agileconfig_server.xbaby.xyz";

            new Thread(() =>
            {
                var client = new ConfigClient(appid, secret, servers, "TEST", new MyLogger());
                client.ConfigChanged += (e) =>
                {
                    if (e != null)
                    {
                        Console.WriteLine("Client_ConfigChanged, action {0} key {1}", e.Action, e.Key);
                    }
                };
                client.ConnectAsync().RunSynchronously();
                while (true)
                {
                    Thread.Sleep(5000);
                    Console.WriteLine("---------------------------");
                    foreach (string key in client.Data.Keys)
                    {
                        var val = client[key];
                        Console.WriteLine("{0} : {1}", key, val);
                    }
                }
            }).Start();


            Console.ReadLine();
        }
    }
}
