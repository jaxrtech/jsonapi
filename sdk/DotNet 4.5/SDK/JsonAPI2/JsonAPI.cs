using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonApi2
{
    /// <summary>
    /// Client API used to interact with the JSON API available on a Minecraft server
    /// </summary>
    public class JsonApi
    {
        /// <summary>
        /// The host address of the server to connect to.
        /// </summary>
        public string Host { get; private set; }

        /// <summary>
        /// The port on the server to connect to.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// The username used on login to the server.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// The password used on login to the server.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// The salt used on login to the server.
        /// </summary>
        public string Salt { get; private set; }

        /// <summary>
        /// If the server is connected
        /// </summary>
        public bool Connected { get; private set; }

        /// <summary>
        /// Event raised when data is received from the server.
        /// </summary>
        public event EventHandler<StreamDataReceivedEventArgs> StreamDataReceived;

        private event EventHandler Connection;

        private readonly TcpClient tcpClient = new TcpClient();
        private readonly List<long> streams = new List<long>();
        private bool acceptStreamResponses = false;
        private StreamReader streamReader;
        private StreamWriter streamWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonApi"/> class.
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="port">The port.</param>
        /// <param name="username">The username.</param>
        /// <param name="password">The password.</param>
        /// <param name="salt">The salt.</param>
        public JsonApi(string host, int port, string username, string password, string salt = "")
        {
            Host = host;
            Port = port;
            Username = username;
            Password = password;
            Salt = salt;
            Connected = false;

            Connection += OnConnection;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonApi"/> class.
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="port">The port.</param>
        public JsonApi(string host, int port) : this(host, port, null, null, null)
        {
        }

        /// <summary>
        /// Connects to the server asynchronously.
        /// </summary>
        /// <exception cref="SocketException">An error occurred connecting to the server.</exception>
        public async Task ConnectAsync()
        {
            await ConnectAsync(Host, Port);
        }

        /// <summary>
        /// Connects to the specified server asynchronously.
        /// </summary>
        /// <param name="host">The hostname of the server to connect to.</param>
        /// <param name="port">The port of the server to connect to.</param>
        public async Task ConnectAsync(string host, int port)
        {
            Host = host;
            Port = port;

            await tcpClient.ConnectAsync(Host, Port + 1);
            ConfigureConnection();
        }

        private void ConfigureConnection()
        {
            streamWriter = new StreamWriter(tcpClient.GetStream());
            streamReader = new StreamReader(tcpClient.GetStream());
            streamWriter.AutoFlush = true;

            Connected = true;
            acceptStreamResponses = true;
            Connection(this, null);
        }

        /// <summary>
        /// Disconnects from the server.
        /// </summary>
        public void Disconnect()
        {
            tcpClient.Client.Close();
            tcpClient.Close();
        }

        /// <summary>
        /// Generate a key for use with Call().
        /// </summary>
        /// <param name="method">The method to call.</param>
        /// <returns>The generated key.</returns>
        public string MakeKey(string method)
        {
            return MakeKey(method, Username, Password, Salt);
        }

        /// <summary>
        /// Generate a key for use with Call().
        /// </summary>
        /// <param name="method">The method to call.</param>
        /// <param name="username">The server's username.</param>
        /// <param name="password">The server's password.</param>
        /// <param name="salt">The server's salt.</param>
        /// <returns>The generated key.</returns>
        public string MakeKey(string method, string username, string password, string salt = "")
        {
            byte[] bytes = Encoding.UTF8.GetBytes(username + method + password + salt);
            byte[] hash = SHA256.Create().ComputeHash(bytes);
            string key = BitConverter.ToString(hash).Replace("-", "").ToLower();

            return key;
        }

        /// <summary>
        /// Calls a method on the server.
        /// </summary>
        /// <param name="method">The method to call.</param>
        /// <param name="key">The key to use. A key will be generated if left null.</param>
        /// <param name="args">Arguments to pass to the server.</param>
        /// <returns>A string if there was an error, otherwise the object returned by the server.</returns>
        public async Task<dynamic> CallAsync(string method, string key = null, params object[] args)
        {
            CheckForInitialConnection();

            if (key == null)
                key = MakeKey(method);

            string requestUrl = FormCallUrl(method, args, key);
            dynamic response = await GetResponceAsync(requestUrl);

            // TODO: Some fancy ApiException would be great right here
            string result;
            if (response.result == "error")
                result = response.error;
            else
                result = response.success;

            return result;
        }

        /// <summary>
        /// Calls multiple methods on the server.
        /// </summary>
        /// <param name="methods">A string[] of methods to call on the server.</param>
        /// <param name="key">The key to use for this request. A key will be generated if left null.</param>
        /// <param name="args">Arguments to pass to the server.</param>
        /// <returns>A dictionary containing the results of the method calls.</returns>
        public async Task<Dictionary<string, dynamic>> CallMultipleAsync(string[] methods, string key = null,
                                                                         params object[][] args)
        {
            CheckForInitialConnection();

            string jsonMethods = JsonConvert.SerializeObject(methods);
            if (key == null)
                key = MakeKey(jsonMethods);

            var requestUrl = FormMultipleCallUrl(jsonMethods, args, key);

            // Send the request
            dynamic response = await GetResponceAsync(requestUrl);

            // The multiple calls get returned in a dictionary.
            // Key   => "source", the name of the original method that was called 
            // Value => "success", the actual return value from the method call
            var result = new Dictionary<string, dynamic>();
            foreach (JObject obj in response)
            {
                result.Add((string) obj["source"], obj["success"]);
            }

            return result;
        }

        private static string FormMultipleCallUrl(string methods, object[][] args, string key)
        {
            // Serialize the JSON to string
            var writer = new StringWriter();
            new JsonSerializer().Serialize(writer, args);

            long now = DateTime.Now.Ticks;
            string requestUrl = string.Format(
                "/api/call-multiple?method={0}&args={1}&key={2}&tag={3}",
                Uri.EscapeDataString(methods),
                Uri.EscapeDataString(writer.ToString()),
                key, now);
            return requestUrl;
        }

        private static string FormCallUrl(string method, object args, string key)
        {
            var writer = new StringWriter();
            new JsonSerializer().Serialize(writer, args);

            long now = DateTime.Now.Ticks;
            string requestUrl = "/api/call?method=" + method
                                + "&args=" + Uri.EscapeDataString(writer.ToString())
                                + "&key=" + key
                                + "&tag=" + now;
            return requestUrl;
        }

        private async Task<dynamic> GetResponceAsync(string requestUrl)
        {
            await streamWriter.WriteAsync(requestUrl);
            string line = await streamReader.ReadLineAsync();
            var response = JsonConvert.DeserializeObject<dynamic>(line);

            return response;
        }

        /// <summary>
        /// Subscribe to a stream source, which can be viewed with the StreamDataReceived event handler.
        /// </summary>
        /// <param name="source">The source to subscribe to. Ex: "console", "chat", or "connections".</param>
        /// <param name="key">The key to use to subscribe. A key will be generated if left null.</param>
        /// <param name="sendPrevious">
        /// Whether or not to send the previous 50 items along with the  most recent. These will be
        /// sent as any other stream message through the StreamDataReceived event.
        /// </param>
        public void Subscribe(string source, string key = null, bool sendPrevious = false)
        {
            CheckForInitialConnection();

            if (key == null)
                key = MakeKey(source, Username, Password, Salt);

            long now = DateTime.Now.Ticks;
            string requestUrl = string.Format(
                "/api/subscribe?source={0}&key={1}&show_previous={2}&tag={3}",
                source, key, sendPrevious, now);

            streams.Add(now);
            streamWriter.WriteLine(requestUrl);
        }

        /// <summary>
        /// Checks if there was ever a connection to the server.
        /// Throws an InvalidOperationException otherwise.
        /// </summary>
        private void CheckForInitialConnection()
        {
            if (!Connected)
            {
                throw new InvalidOperationException(
                    "Connection to server was never initially established. Use JsonAPI.Connect()");
                
            }
        }

        private void OnConnection(object sender, EventArgs eventArgs)
        {
            new Thread(StreamReadThread).Start();
        }

        /// <summary>
        /// The thread which reads and processes network data.
        /// </summary>
        private void StreamReadThread()
        {
            while (acceptStreamResponses && tcpClient.Connected)
            {
                string line = streamReader.ReadLine();
                var response = JsonConvert.DeserializeObject<dynamic>(line);

                // Drop the response if we never subscribed to it in the first place
                if (!streams.Contains((long) response.tag)) continue;

                StreamDataReceivedEventArgs eventArgs;
                // TODO: This should probably throw an APIExecption or something instead
                if (response.result == "error")
                {
                    eventArgs = new StreamDataReceivedEventArgs(true, (string) response.source, response.error);
                }
                else
                {
                    eventArgs = new StreamDataReceivedEventArgs(false, (string) response.source, response.success);
                }

                StreamDataReceived(this, eventArgs);
            }
        }
    }

    /// <summary>
    /// The EventArgs containing the data returned from a stream response.
    /// </summary>
    public class StreamDataReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Whether or not the stream data was the result of an error.
        /// </summary>
        public bool Error { get; private set; }

        /// <summary>
        /// The data returned with the stream message. If Error is true, then it is the error message.
        /// </summary>
        public dynamic Data { get; private set; }

        /// <summary>
        /// The source which the stream message came from.
        /// </summary>
        public string Source { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamDataReceivedEventArgs"/> class.
        /// </summary>
        /// <param name="error">If the data returned is an error message.</param>
        /// <param name="source">The source method call.</param>
        /// <param name="data">The data returned.</param>
        public StreamDataReceivedEventArgs(bool error, string source, dynamic data)
        {
            Error = error;
            Data = data;
            Source = source;
        }
    }
}