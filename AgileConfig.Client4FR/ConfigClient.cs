﻿using AgileConfig.Protocol;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection; // 用于反射获取IIS网站根目录
namespace AgileConfig.Client
{
    public enum ConnectStatus
    {
        Disconnected,
        Connecting,
        Connected,
    }

    public class ConfigClient : IConfigClient
    {
        public static IConfigClient Instance = null;
        public ConfigClient(string appId, string secret, string serverNodes, string env, ILogger logger = null)
        {
            this.Logger = logger;
            if (string.IsNullOrEmpty(appId))
            {
                throw new ArgumentNullException(nameof(appId));
            }
            if (string.IsNullOrEmpty(serverNodes))
            {
                throw new ArgumentNullException(nameof(serverNodes));
            }
            this._AppId = appId;
            this._Secret = secret;
            this._ServerNodes = serverNodes;
            this._Env = string.IsNullOrEmpty(env) ? "" : env.ToUpper();
        }

        private int _WebsocketReconnectInterval = 10;
        private int _WebsocketHeartbeatInterval = 30;

        public ILogger Logger { get; set; }
        private string _ServerNodes;
        private string _AppId;
        private string _Secret;
        private string _Env;
        private bool _isAutoReConnecting = false;
        private bool _isWsHeartbeating = false;

        private WebSocket4Net.WebSocket _WebsocketClient;
        private bool _adminSayOffline = false;
        private bool _isLoadFromLocal = false;
        private ConcurrentDictionary<string, string> _data = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<ConfigItem> _configs = new List<ConfigItem>();

        public ConnectStatus Status { get; private set; }

        public string Name
        {
            get;
            set;
        }
        public string Tag
        {
            get;
            set;
        }

        public string AppId
        {
            get
            {
                return _AppId;
            }
        }
        public string Secret
        {
            get
            {
                return _Secret;
            }
        }
        public string Env
        {
            get
            {
                return _Env;
            }
        }

        /// <summary>
        /// 是否读取的事本地缓存的配置
        /// </summary>
        public bool IsLoadFromLocal
        {
            get
            {
                return _isLoadFromLocal;
            }
        }
        /// <summary>
        /// 配置项修改事件
        /// </summary>
        public event Action<ConfigChangedArg> ConfigChanged;
        /// <summary>
        /// 所有的配置项最后都会转换为字典
        /// </summary>
        public ConcurrentDictionary<string, string> Data => _data;

        public string this[string key]
        {
            get
            {
                Data.TryGetValue(key, out string val);
                return val;
            }
        }

        /// <summary>
        /// 根据键值获取配置值
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public string Get(string key)
        {
            Data.TryGetValue(key, out string val);
            return val;
        }

        /// <summary>
        /// 获取分组配置信息
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        public List<ConfigItem> GetGroup(string groupName)
        {
            if (_configs == null)
            {
                return null;
            }

            return _configs.Where(x => x.group == groupName).ToList();
        }

        /// <summary>
        /// 连接服务端
        /// </summary>
        /// <returns></returns>
        public Task<bool> ConnectAsync()
        {
            if (this.Status == ConnectStatus.Connected
                || this.Status == ConnectStatus.Connecting
                || _WebsocketClient?.State == WebSocket4Net.WebSocketState.Open)
            {
                return Task.FromResult(true);
            }
            else
            {
                _WebsocketClient?.Dispose();
                _WebsocketClient = default;
                this.Status = ConnectStatus.Disconnected;
            }

            TryConnectWebsocketAsync(new RandomServers(_ServerNodes), () =>
            {
                WebsocketHeartbeatAsync();
            },
            () =>
            {
            }
            );
            //不管websocket是否成功，都去拉一次配置
            Load();
            //设置自动重连
            AutoReConnect();

            return Task.FromResult(this.Status == ConnectStatus.Connected);
        }

        private void TryConnectWebsocketAsync(RandomServers randomServers, Action connected, Action allServerTestFailed)
        {
            this.Status = ConnectStatus.Connecting;
            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("appid", _AppId));
            headers.Add(new KeyValuePair<string, string>("Authorization", GenerateBasicAuthorization(_AppId, _Secret)));
            headers.Add(new KeyValuePair<string, string>("env", _Env));

            try
            {
                if (randomServers.IsComplete)
                {
                    if (allServerTestFailed != null)
                    {
                        allServerTestFailed();
                        return;
                    }
                }

                var server = randomServers.Next();
                var websocketServerUrl = CreateWSUrl(server);

                Logger?.LogTrace("client try connect to server " + websocketServerUrl);

                _WebsocketClient = new WebSocket4Net.WebSocket(websocketServerUrl, "", null, headers);
                _WebsocketClient.MessageReceived += _WebsocketClient_MessageReceived;
                _WebsocketClient.Closed += (s, e) =>
                {
                    this.Status = ConnectStatus.Disconnected;
                    Logger?.LogTrace("websocket client closed .");
                };
                _WebsocketClient.Error += (s, e) =>
                {
                    TryConnectWebsocketAsync(randomServers, connected, allServerTestFailed);
                    Logger?.LogError(e.Exception, "websocket client occur error .");
                };
                _WebsocketClient.Opened += (s, e) =>
                {
                    Logger?.LogTrace("websocket client opened .");
                    this.Status = ConnectStatus.Connected;
                    if (connected != null)
                    {
                        connected();
                    }
                };
                _WebsocketClient.Open();
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "client try connect to server occur error .");
            }
        }

        private string CreateWSUrl(string serverUrl)
        {
            var clientName = string.IsNullOrEmpty(Name) ? "" : System.Web.HttpUtility.UrlEncode(Name);
            var tag = string.IsNullOrEmpty(Tag) ? "" : System.Web.HttpUtility.UrlEncode(Tag);
            var websocketServerUrl = "";
            if (serverUrl.StartsWith("https:", StringComparison.CurrentCultureIgnoreCase))
            {
                websocketServerUrl = serverUrl.Replace("https:", "wss:").Replace("HTTPS:", "wss:");
            }
            else
            {
                websocketServerUrl = serverUrl.Replace("http:", "ws:").Replace("HTTP:", "ws:");
            }
            websocketServerUrl = websocketServerUrl + (websocketServerUrl.EndsWith("/") ? "ws" : "/ws");
            websocketServerUrl += "?";
            websocketServerUrl += "client_name=" + clientName;
            websocketServerUrl += "&client_tag=" + tag;

            return websocketServerUrl;
        }

        private void _WebsocketClient_MessageReceived(object sender, WebSocket4Net.MessageReceivedEventArgs e)
        {
            var msg = e?.Message;
            Logger?.LogTrace($"client receive message ' {msg} '");
            ProcessMessage(msg);
        }

        private void LoadConfigsFromLoacl()
        {
            var fileContent = ReadConfigsFromLocal();
            if (!string.IsNullOrEmpty(fileContent))
            {
                ReloadDataDictFromContent(fileContent);
                _isLoadFromLocal = true;
                Logger?.LogTrace("client load all configs from local file .");
            }
        }

        /// <summary>
        /// 开启一个线程来初始化Websocket Client，并且5s一次进行检查是否连接打开状态，如果不是则尝试重连。
        /// </summary>
        /// <returns></returns>
        private void AutoReConnect()
        {
            if (_isAutoReConnecting)
            {
                return;
            }
            _isAutoReConnecting = true;

            Thread th = new Thread(() =>
           {
               while (true)
               {
                   Thread.Sleep(1000 * _WebsocketReconnectInterval);

                   if (this.Status == ConnectStatus.Connected || this.Status == ConnectStatus.Connecting)
                   {
                       continue;
                   }

                   try
                   {
                       _WebsocketClient?.Dispose();
                       _WebsocketClient = null;
                       this.Status = ConnectStatus.Disconnected;

                       if (_adminSayOffline)
                       {
                           break;
                       }

                       TryConnectWebsocketAsync(new RandomServers(_ServerNodes), () =>
                       {
                           Load();
                           WebsocketHeartbeatAsync();
                       }, () =>
                       {
                       });
                   }
                   catch (Exception ex)
                   {
                       Logger?.LogError(ex, "client try to connected to server failed.");
                   }
               }
           });
            th.Start();
        }

        private string GenerateBasicAuthorization(string appId, string secret)
        {
            var txt = $"{appId}:{secret}";
            var data = Encoding.UTF8.GetBytes(txt);
            return "Basic " + Convert.ToBase64String(data);
        }
        /// <summary>
        /// 开启一个线程30s进行一次心跳
        /// </summary>
        /// <returns></returns>
        public void WebsocketHeartbeatAsync()
        {
            if (_isWsHeartbeating)
            {
                return;
            }
            _isWsHeartbeating = true;

            new Thread(() =>
           {
               while (true)
               {
                   Thread.Sleep(1000 * _WebsocketHeartbeatInterval);
                   if (_adminSayOffline)
                   {
                       break;
                   }
                   if (_WebsocketClient?.State == WebSocket4Net.WebSocketState.Open)
                   {
                       try
                       {
                           //这里由于多线程的问题，WebsocketClient有可能在上一个if判断成功后被置空或者断开，所以需要try一下避免线程退出
                           _WebsocketClient.Send("ping");
                           Logger?.LogTrace("client send 'ping' to server by websocket .");
                       }
                       catch (Exception ex)
                       {
                           Logger?.LogError(ex, "client try to send Heartbeat to server failed.");
                       }
                   }
               }
           }).Start();
        }


        /// <summary>
        /// 最终处理服务端推送的消息
        /// </summary>
        private void ProcessMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg) || msg == "0")
            {
                return;
            }
            if (msg.StartsWith("V:"))
            {
                var version = msg.Substring(2, msg.Length - 2);
                var localVersion = this.DataMd5Version();
                if (version != localVersion)
                {
                    //如果数据库版本跟本地版本不一致则直接全部更新
                    Load();
                }
                return;
            }
            try
            {
                var action = JsonConvert.DeserializeObject<WebsocketAction>(msg);
                if (action != null)
                {
                    var dict = Data;
                    var itemKey = "";
                    if (action.Item != null)
                    {
                        itemKey = GenerateKey(action.Item);
                    }
                    switch (action.Action)
                    {
                        case ActionConst.Add:
                            dict.AddOrUpdate(itemKey, action.Item.value, (k, v) => { return action.Item.value; });
                            NoticeChangedAsync(ActionConst.Add, itemKey);
                            break;
                        case ActionConst.Update:
                            if (action.OldItem != null)
                            {
                                dict.TryRemove(GenerateKey(action.OldItem), out string oldV);
                            }
                            dict.AddOrUpdate(itemKey, action.Item.value, (k, v) => { return action.Item.value; });
                            NoticeChangedAsync(ActionConst.Update, itemKey);
                            break;
                        case ActionConst.Remove:
                            dict.TryRemove(itemKey, out string oldV1);
                            NoticeChangedAsync(ActionConst.Remove, itemKey);
                            break;
                        case ActionConst.Offline:
                            _adminSayOffline = true;
                            _WebsocketClient.Close();
                            this.Status = ConnectStatus.Disconnected;
                            Logger?.LogTrace("client offline because admin console send a command 'offline'");
                            NoticeChangedAsync(ActionConst.Offline);
                            break;
                        case ActionConst.Reload:
                            if (Load())
                            {
                                NoticeChangedAsync(ActionConst.Reload);
                            };
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Cannot handle message {msg}");
            }
        }

        private void NoticeChangedAsync(string action, string key = "")
        {
            if (ConfigChanged == null)
            {
                return;
            }
            Task.Run(() =>
            {
                ConfigChanged(new ConfigChangedArg(action, key));
            });
        }

        private string GenerateKey(ConfigItem item)
        {
            var key = new StringBuilder();
            if (!string.IsNullOrEmpty(item.group))
            {
                key.Append(item.group + ":");
            }
            key.Append(item.key);

            return key.ToString();
        }

        /// <summary>
        /// 通过http从server拉取所有配置到本地
        /// </summary>
        public bool Load()
        {
            int failCount = 0;
            var randomServer = new RandomServers(_ServerNodes);
            while (!randomServer.IsComplete)
            {
                var url = randomServer.Next();
                try
                {
                    var op = new AgileHttp.RequestOptions()
                    {
                        Headers = new Dictionary<string, string>()
                        {
                            {"appid", _AppId },
                            {"Authorization", GenerateBasicAuthorization(_AppId, _Secret) }
                        }
                    };
                    var apiUrl = url + (url.EndsWith("/") ? "" : "/") + $"api/config/app/{_AppId}?env={_Env}";
                    using (var result = AgileHttp.HTTP.Send(apiUrl, "GET", null, op))
                    {
                        if (result.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            var respContent = result.GetResponseContent();
                            ReloadDataDictFromContent(respContent);
                            WriteConfigsToLocal(respContent);
                            _isLoadFromLocal = false;

                            Logger?.LogTrace($"client load all the configs from {apiUrl} successful . try count: {failCount}.");
                            return true;
                        }
                        else
                        {
                            //load remote configs err .
                            var ex = result.Exception ?? new Exception("client try to load all the configs but failed .");
                            throw ex;
                        }
                    }
                }
                catch
                {
                    failCount++;
                }
            }
            if (failCount == randomServer.ServerCount)
            {
                LoadConfigsFromLoacl();
            }
            return false;
        }

        public void LoadConfigs(List<ConfigItem> configs)
        {
            Data.Clear();
            _configs.Clear();
            if (configs != null)
            {
                _configs = configs;
                _configs.ForEach(c =>
                {
                    var key = GenerateKey(c);
                    string value = c.value;
                    Data.TryAdd(key.ToString(), value);
                });
            }
        }

        private void ReloadDataDictFromContent(string content)
        {
            var configs = JsonConvert.DeserializeObject<List<ConfigItem>>(content);
            LoadConfigs(configs);
        }

        /// <summary>
        /// 获取本地缓存文件的完整路径，优先使用网站根目录而非IIS系统目录
        /// 使用多重回退策略确保在不同环境下都能找到合适的缓存目录
        /// </summary>
        private string LocalCacheFileName
        {
            get
            {
                var fileName = $"{_AppId}.agileconfig.client.configs.cache";

                try
                {
                    // 尝试获取网站根目录（兼容不同的.NET版本），优先级最高
                    var webRootPath = GetWebRootPath();
                    if (!string.IsNullOrEmpty(webRootPath))
                    {
                        var fullPath = Path.Combine(webRootPath, fileName);
                        TestWritePermission(Path.GetDirectoryName(fullPath));
                        return fullPath;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Cannot use web root directory for cache file: {ex.Message}");
                }

                try
                {
                    // 回退到应用程序域基目录，第二优先级
                    var appDomainPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                    TestWritePermission(Path.GetDirectoryName(appDomainPath));
                    return appDomainPath;
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Cannot use app domain directory for cache file: {ex.Message}");
                }

                try
                {
                    // 回退到程序集所在目录，第三优先级
                    var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(assemblyPath))
                    {
                        var fullPath = Path.Combine(assemblyPath, fileName);
                        TestWritePermission(assemblyPath);
                        return fullPath;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Cannot use assembly directory for cache file: {ex.Message}");
                }

                try
                {
                    // 回退到临时目录，第四优先级
                    var tempPath = Path.Combine(Path.GetTempPath(), fileName);
                    TestWritePermission(Path.GetDirectoryName(tempPath));
                    return tempPath;
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Cannot use temp directory for cache file: {ex.Message}");
                }

                // 最后回退到当前目录
                return fileName;
            }
        }

        /// <summary>
        /// 获取Web应用程序根目录路径，兼容多种.NET版本和环境
        /// 通过反射机制避免编译时依赖，支持.NET Framework和.NET Core/.NET 5+
        /// </summary>
        private string GetWebRootPath()
        {
            try
            {
                // 方法1 - 尝试使用 System.Web.Hosting.HostingEnvironment（.NET Framework）
                var hostingEnvironmentType = Type.GetType("System.Web.Hosting.HostingEnvironment, System.Web");
                if (hostingEnvironmentType != null)
                {
                    var isHostedProperty = hostingEnvironmentType.GetProperty("IsHosted");
                    var applicationPhysicalPathProperty = hostingEnvironmentType.GetProperty("ApplicationPhysicalPath");

                    if (isHostedProperty != null && applicationPhysicalPathProperty != null)
                    {
                        var isHosted = (bool)isHostedProperty.GetValue(null);
                        if (isHosted)
                        {
                            var path = (string)applicationPhysicalPathProperty.GetValue(null);
                            if (!string.IsNullOrEmpty(path))
                            {
                                return path;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogTrace($"Failed to get path via HostingEnvironment: {ex.Message}");
            }

            try
            {
                // 方法2 - 尝试通过HttpContext获取（如果在Web环境中）
                var httpContextType = Type.GetType("System.Web.HttpContext, System.Web");
                if (httpContextType != null)
                {
                    var currentProperty = httpContextType.GetProperty("Current");
                    if (currentProperty != null)
                    {
                        var current = currentProperty.GetValue(null);
                        if (current != null)
                        {
                            var serverProperty = current.GetType().GetProperty("Server");
                            if (serverProperty != null)
                            {
                                var server = serverProperty.GetValue(current);
                                var mapPathMethod = server?.GetType().GetMethod("MapPath", new[] { typeof(string) });
                                if (mapPathMethod != null)
                                {
                                    var path = (string)mapPathMethod.Invoke(server, new object[] { "~/" });
                                    if (!string.IsNullOrEmpty(path))
                                    {
                                        return path;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogTrace($"Failed to get path via HttpContext: {ex.Message}");
            }

            try
            {
                // 方法3 - 尝试从环境变量和目录结构推断（IIS环境）
                var appPhysicalPath = Environment.GetEnvironmentVariable("APP_POOL_ID");
                if (!string.IsNullOrEmpty(appPhysicalPath))
                {
                    // 在IIS环境中，尝试通过其他方式获取应用程序路径
                    var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    if (baseDirectory.Contains("\\bin\\") || baseDirectory.EndsWith("\\bin"))
                    {
                        // 如果当前目录是bin目录，向上一级就是网站根目录
                        var webRoot = Path.GetDirectoryName(baseDirectory.TrimEnd('\\'));
                        if (webRoot.EndsWith("\\bin"))
                        {
                            webRoot = Path.GetDirectoryName(webRoot);
                        }
                        return webRoot;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogTrace($"Failed to get path via environment variables: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 测试目录写入权限，确保可以创建缓存文件
        /// </summary>
        /// <param name="directoryPath">目录路径</param>
        private void TestWritePermission(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
                return;

            // 确保目录存在
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // 测试写入权限
            var testFile = Path.Combine(directoryPath, $"test_write_{Guid.NewGuid()}.tmp");
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException($"No write permission for directory: {directoryPath}");
            }
            catch (DirectoryNotFoundException)
            {
                throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
            }
        }


        private void WriteConfigsToLocal(string configContent)
        {
            try
            {
                if (string.IsNullOrEmpty(configContent))
                {
                    return;
                }

                var filePath = LocalCacheFileName;

                // 确保目录存在
                var directory = Path.GetDirectoryName(filePath);

                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(LocalCacheFileName, configContent);
                Logger?.LogTrace($"client cache all configs to local file: {filePath}"); // 增加文件路径日志
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "client try to cache all configs to local but fail .");
            }
        }

        private string ReadConfigsFromLocal()
        {
            try
            {
                var filePath = LocalCacheFileName;
                if (!File.Exists(filePath))
                {
                    return "";
                }

                var content = File.ReadAllText(filePath);
                Logger?.LogTrace($"client read configs from local file: {filePath}"); // 增加文件路径日志
                return content;
            }
            catch (Exception ex) // 异常处理
            {
                Logger?.LogError(ex, "client try to read configs from local file but fail .");
                return "";
            }
        }

        private string DataMd5Version()
        {
            var keyStr = string.Join("&", Data.Keys.ToArray().OrderBy(k => k));
            var valueStr = string.Join("&", Data.Values.ToArray().OrderBy(v => v));
            var txt = $"{keyStr}&{valueStr}";

            using (var md5 = MD5.Create())
            {
                var inputBytes = Encoding.ASCII.GetBytes(txt);
                var hashBytes = md5.ComputeHash(inputBytes);

                // Convert the byte array to hexadecimal string
                var sb = new StringBuilder();
                for (var i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }

                return sb.ToString();
            }
        }


    }
}
