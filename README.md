# AgileConfig.Client4FR
AgileConfig client 的 framework 版本 。因为使用 standard2 编写的 websocket client 在 framework 上有 cpu 100% 的风险，所以另外开一个仓库使用 supersocket 的 websocketclient 来实现长连接。
同时移除了 IConfiguration , IServiceCollection 等 framework 无关的依赖。
## 使用
nuget 安装
```
Install-Package AgileConfig.Client4FR -Version 1.0.2
```
使用client
```
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");

            var appid = "test_app";
            var secret = "test_app";
            var servers = "http://agileconfig_server.xbaby.xyz";

            new Thread(() =>
            {
                var client = new ConfigClient(appid, secret, servers, new MyLogger());
                client.ConfigChanged += (e)=> {
                    if (e != null)
                    {
                        Console.WriteLine("Client_ConfigChanged, action {0} key {1}", e.Action, e.Key);
                    }
                };
                client.ConnectAsync().GetAwaiter().GetResult();
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
```
