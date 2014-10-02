using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SocketIOClient.Eventing;
using SocketIOClient.Messages;
using SocketIOClient.Transports;
using WebSocket4Net;

namespace SocketIOClient
{
    using SocketIOClient.Helpers;

    /// <summary>
    /// Class to emulate the socket.io javascript client capabilities for .net classes
    /// </summary>
    /// <exception cref = "ArgumentException">Connection for wss or https urls</exception>  
    public partial class Client : IDisposable, IClient
    {
        private Timer socketHeartBeatTimer; // HeartBeat timer 
        private Task dequeuOutBoundMsgTask;
        private BlockingCollection<string> outboundQueue;
        private bool connecting = false;
        private int retryConnectionCount = 0;
        private int retryConnectionAttempts = 3;
        private readonly static object padLock = new object(); // allow one connection attempt at a time

        private string userAgent = "";
        private string origin = "";

        // List of transports in fallback order
        public List<TransportType> TransportPeferenceTypes = new List<TransportType>();
        private List<TransportType> allowedTransports;

        /// <summary>
        /// Uri of Websocket server
        /// </summary>
        protected Uri uri;
        /// <summary>
        /// Underlying WebSocket implementation
        /// </summary>
        public ITransport ioTransport;
        /// <summary>
        /// RegistrationManager for dynamic events
        /// </summary>
        protected RegistrationManager registrationManager;  // allow registration of dynamic events (event names) for client actions

        // Events
        /// <summary>
        /// Opened event comes from the underlying websocket client connection being opened.  This is not the same as socket.io returning the 'connect' event
        /// </summary>
        public event EventHandler Opened;
        public event EventHandler<MessageEventArgs> Message;
        public event EventHandler ConnectionRetryAttempt;
        public event EventHandler HeartBeatTimerEvent;

        /// <summary>
        /// <para>The underlying websocket connection has closed (unexpectedly)</para>
        /// <para>The Socket.IO service may have closed the connection due to a heartbeat timeout, or the connection was just broken</para>
        /// <para>Call the client.Connect() method to re-establish the connection</para>
        /// </summary>
        public event EventHandler SocketConnectionClosed;
        public event EventHandler<ErrorEventArgs> Error;

        /// <summary>
        /// ResetEvent for Outbound MessageQueue Empty Event - all pending messages have been sent
        /// </summary>
        public ManualResetEvent MessageQueueEmptyEvent = new ManualResetEvent(true);

        /// <summary>
        /// Connection Open Event
        /// </summary>
        public ManualResetEvent ConnectionOpenEvent = new ManualResetEvent(false);

        /// <summary>
        /// Number of reconnection attempts before raising SocketConnectionClosed event - (default = 3)
        /// </summary>
        public int RetryConnectionAttempts
        {
            get { return this.retryConnectionAttempts; }
            set { this.retryConnectionAttempts = value; }
        }

        /// <summary>
        /// Value of the last error message text  
        /// </summary>
        public string LastErrorMessage = "";

        /// <summary>
        /// Represents the initial handshake parameters received from the socket.io service (SID, HeartbeatTimeout etc)
        /// </summary>
        public IOHandshake HandShake { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether debug trace information is sent.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [debug]; otherwise, <c>false</c>.
        /// </value>
        public bool Debug { get; set; }

        /// <summary>
        /// Returns boolean of ReadyState == WebSocketState.Open
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return this.ReadyState == WebSocketState.Open;
            }
        }

        /// <summary>
        /// Connection state of websocket client: None, Connecting, Open, Closing, Closed
        /// </summary>
        public WebSocketState ReadyState
        {
            get
            {
                // TraceInformation("iotransport {0}", ioTransport == null ? "null" : ioTransport.State.ToString());

                return this.ioTransport == null 
                    ? this.connecting ? WebSocketState.Connecting : WebSocketState.None 
                    : this.ioTransport.State;
            }
        }

        // Constructors
        public Client(string url)
            : this(url, null)
        {
        }
        public Client(string url, NameValueCollection headers)
        {
            this.Debug = true;

            this.uri = new Uri(url);
            this.Headers = headers;

            this.Init();
        }

        private NameValueCollection Headers { get; set; }

        private void Init()
        {
            if (this.TaskCanceler != null) this.TaskCanceler.Cancel();
            this.TaskCanceler = null;

            var regMgr = this.registrationManager;
            var q = this.outboundQueue;

            this.HandShake = new IOHandshake(this.uri, this.Headers);
            this.registrationManager = new RegistrationManager();
            this.outboundQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());

            if (regMgr != null) regMgr.Dispose();
            if (q != null) q.Dispose();

            this.TaskCanceler = new CancellationTokenSource();
            this.dequeuOutBoundMsgTask =
                Task.Factory.StartNew(
                    this.DequeuOutboundMessages,
                    this.TaskCanceler.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(t => t.HandleIfFailed());
        }

        private CancellationTokenSource TaskCanceler { get; set; }

        private void DeInit()
        {
            if (this.TaskCanceler != null) this.TaskCanceler.Cancel();
            this.TaskCanceler = null;

            var regMgr = this.registrationManager;
            var q = this.outboundQueue;

            this.HandShake = new IOHandshake(this.uri, this.Headers);
            this.registrationManager = new RegistrationManager();
            this.outboundQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());

            if (regMgr != null) regMgr.Dispose();
            if (q != null) q.Dispose();
        }

        /// <summary>
        /// Initiate the connection with Socket.IO service
        /// </summary>
        public void Connect()
        {
            var readyState = this.ReadyState;
            if (readyState == WebSocketState.Connecting || readyState == WebSocketState.Open)
            {
                this.TraceWarning("Cannot connect, ReadyState is {0}", readyState);
                return;
            }

            try
            {
                this.TraceInformation("Connect");
                this.connecting = true;

                this.Init();

                if (this.TransportPeferenceTypes.Count == 0)
                {
                    var transports = (TransportType[])Enum.GetValues(typeof(TransportType));
                    this.TransportPeferenceTypes.AddRange(new List<TransportType>(transports)); 
                }

                this.ConnectionOpenEvent.Reset();
                    
                this.RequestHandshake(this.uri).ContinueWith(task =>
                    {
                        if (task.IsFaulted || task.IsCanceled || task.Exception != null)
                        {
                            this.TraceError("RequestHandshake Failed");
                            this.OnConnectionError(new ErrorEventArgs("Handshake error", task.Exception.HandleAndGetFirst()));
                            this.connecting = false;
                            task.HandleIfFailed();
                            return;
                        }

                        this.TraceInformation("RequestHandshake > ContinueWith");

                        if (string.IsNullOrWhiteSpace(this.HandShake.SID) ||
                            this.HandShake.HadError)
                        {
                            this.LastErrorMessage = string.Format("Error initializing handshake with {0}", this.uri.ToString());
                            this.OnConnectionError(new ErrorEventArgs(this.LastErrorMessage, new Exception()));
                        }
                        else
                        {
                            this.allowedTransports = new List<TransportType>(this.TransportPeferenceTypes.Union(this.HandShake.AvailableTransports));
                            this.ResolveTransport()
                                .ContinueWith(r =>
                                    {
                                        if (r.Exception != null)
                                        {
                                            TraceError("ResolveTransport error: {0}", r.Exception.InnerExceptions.First().Message);
                                            r.HandleIfFailed();
                                        }
                                        else if (r.Result != null)
                                        {
                                            this.ioTransport = r.Result;
                                        }
                                    });
                        }
                        this.connecting = false;
                    });
            }
            catch (Exception ex)
            {
                this.TraceError(string.Format("Connect threw an exception...{0}", ex.Message));
                this.OnErrorEvent(this, new ErrorEventArgs("SocketIO.Client.Connect threw an exception", ex));
                this.connecting = false;
            }
        }

        public void Connect(TransportType transport)
        {
            this.TransportPeferenceTypes.Clear();
            this.TransportPeferenceTypes.Add(transport);

            this.Connect();
        }

        /// <summary>
        /// Triggered when an error occurs during a connection attempt
        /// </summary>
        public event EventHandler<ErrorEventArgs> ConnectionError;

        protected void OnConnectionError(ErrorEventArgs args)
        {
            if (this.ConnectionError != null) this.ConnectionError(this, args);
            this.OnErrorEvent(this, args);
        }

#if NET40
        private Task<ITransport> ResolveTransport()
        {
            return Task<ITransport>.Factory.StartNew(
                () =>
                    {
                        ITransport transport = null;
                        int idx = 0;
                        int totalAvail = allowedTransports.Count;
                        while (idx < allowedTransports.Count)
                        {
                            TransportType type = allowedTransports[idx];
                            try
                            {
                                ITransport _trspt = this.ioTransport = TransportFactory.Transport(type, this.HandShake);

                                this.AttachTransportEvents(_trspt);
                                var nestedTask = _trspt.OpenAsync()
                                .ContinueWith(
                                    task =>
                                        {
                                            task.ThrowIfFailed();

                                            bool status = task.Result;
                                            if (status && _trspt.State == WebSocketState.Open)
                                            {
                                                transport = _trspt;
                                                this.TraceInformation("webSocket Connected via: {0}", type.ToString());
                                            }
                                            else
                                            {
                                                this.DetachTransportEvents(_trspt);
                                            }

                                            return transport;
                                        });

                                return nestedTask.Result;
                            }
                            catch (Exception ex)
                            {
                                //throw new Exception("Transport types have been exhausted", ex);
                                this.TraceError("Resolve-exceptions:" + ex.Message);
                            }
                            finally
                            {
                                idx++;
                            }
                        }
                        if (transport == null)
                        {
                            throw new Exception("Transport types have been exhausted");
                        }
                        return transport;
                    });
        }
#endif


#if NET45
        private async Task<ITransport> ResolveTransport()
        {
            ITransport transport = null;
            int idx = 0;
            int totalAvail = allowedTransports.Count;
            while (idx < allowedTransports.Count)
            {
                TransportType type = allowedTransports[idx];
                try
                {
                    ITransport _trspt = this.ioTransport = TransportFactory.Transport(type, this.HandShake);

                    this.AttachTransportEvents(_trspt);
                    bool status = await _trspt.OpenAsync();
                    if (status && _trspt.State == WebSocketState.Open)
                    {
                        transport = _trspt;
                        this.TraceInformation("webSocket Connected via: {0}", type.ToString());
                        break;
                    }
                    else
                    {
                        this.DetachTransportEvents(_trspt);
                    }
                }
                catch (Exception ex)
                {
                    //throw new Exception("Transport types have been exhausted", ex);
                    this.TraceError("Resolve-exceptions:" + ex.Message);
                }
                finally
                {
                    idx++;
                }
            }
            if (transport == null)
            {
                throw new Exception("Transport types have been exhausted");
            }
            return transport;
        }
#endif



        public IEndPointClient Connect(string endPoint)
        {
            var nsClient = new EndPointClient(this, endPoint);
            this.Connect();
            this.Send(new ConnectMessage(endPoint));
            return nsClient;
        }
        
#if NET40
        protected void ReConnect()
        {
            this.TraceInformation("Reconnecting...");

            this.retryConnectionCount++;

            this.OnConnectionRetryAttemptEvent(this, EventArgs.Empty);

            this.CloseHeartBeatTimer(); // stop the heartbeat time
            this.CloseWebSocketClient();// stop websocket
            this.HandShake.ResetConnection();
            if (this.ioTransport != null)
            {
                this.ioTransport.Handshake.ResetConnection();
                var task = this.ioTransport.Reconnect();
                task.Wait();
                bool connected = task.Result;

                this.TraceInformation(string.Format("\tRetry-Connection successful: {0}", connected));
                if (connected)
                    this.retryConnectionCount = 0;
                else
                {
                    // we didn't connect - try again until exhausted
                    if (this.retryConnectionCount < this.RetryConnectionAttempts)
                    {
                        this.ReConnect();
                    }
                    else
                    {
                        this.Close();
                        this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
                    }
                }
            }
        }
#endif


#if NET45
        protected async void ReConnect()
        {
            this.retryConnectionCount++;

            this.OnConnectionRetryAttemptEvent(this, EventArgs.Empty);

            this.CloseHeartBeatTimer(); // stop the heartbeat time
            this.CloseWebSocketClient();// stop websocket
            this.HandShake.ResetConnection();
            if (ioTransport != null)
            {
                this.ioTransport.Handshake.ResetConnection();
                bool connected = await this.ioTransport.Reconnect();

                Trace.WriteLine(string.Format("\tRetry-Connection successful: {0}", connected));
                if (connected)
                    this.retryConnectionCount = 0;
                else
                {
                    // we didn't connect - try again until exhausted
                    if (this.retryConnectionCount < this.RetryConnectionAttempts)
                    {
                        this.ReConnect();
                    }
                    else
                    {
                        this.Close();
                        this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
                    }
                }
            }
        }
#endif

        private void AttachTransportEvents(ITransport transport)
        {
            transport.Opened += this.WsClientOpenEvent;
            transport.MessageReceived += this.WsClientMessageReceived;
            transport.Error += this.WsClientError;
            transport.Closed += this.WsClientClosed;
        }

        private void DetachTransportEvents(ITransport transport)
        {
            transport.Closed -= this.WsClientClosed;
            transport.MessageReceived -= this.WsClientMessageReceived;
            transport.Error -= WsClientError;
            transport.Opened -= this.WsClientOpenEvent;
        }

        /// <summary>
        /// <para>Asynchronously calls the action delegate on event message notification</para>
        /// <para>Mimicks the Socket.IO client 'socket.on('name',function(data){});' pattern</para>
        /// <para>Reserved socket.io event names available: connect, disconnect, open, close, error, retry, reconnect  </para>
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="action"></param>
        /// <example>
        /// client.On("testme", (data) =>
        ///    {
        ///        Debug.WriteLine(data.ToJson());
        ///    });
        /// </example>
        public virtual void On(
            string eventName,
            Action<IMessage> action)
        {
            this.registrationManager.AddOnEvent(eventName, action);
        }
        public virtual void On(
            string eventName,
            string endPoint,
            Action<IMessage> action)
        {

            this.registrationManager.AddOnEvent(eventName, endPoint, action);
        }

        /// <summary>
        /// <para>Asynchronously sends payload using eventName</para>
        /// <para>payload must a string or Json Serializable</para>
        /// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
        /// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="payload">must be a string or a Json Serializable object</param>
        /// <remarks>ArgumentOutOfRangeException will be thrown on reserved event names</remarks>
        public void Emit(string eventName, dynamic payload, string endPoint = "", Action<dynamic> callback = null)
        {

            string lceventName = eventName.ToLower();
            IMessage msg = null;
            switch (lceventName)
            {
                case "message":
                    if (payload == null)
                    {
                        if (payload is string)
                            msg = new TextMessage() { MessageText = payload };
                        else
                            msg = new JSONMessage(payload);
                    }
                    else
                    {
                        msg = new TextMessage();
                    }
                    this.TraceInformation("Emit message: " + msg.MessageText);
                    this.Send(msg);
                    break;
                case "connect":
                case "disconnect":
                case "open":
                case "close":
                case "error":
                case "retry":
                case "reconnect":
                    throw new System.ArgumentOutOfRangeException(eventName, "Event name is reserved by socket.io, and cannot be used by clients or servers with this message type");
                default:
                    if (!string.IsNullOrWhiteSpace(endPoint) && !endPoint.StartsWith("/"))
                        endPoint = "/" + endPoint;
                    msg = new EventMessage(eventName, payload, endPoint, callback);
                    if (callback != null)
                        this.registrationManager.AddCallBack(msg);

                    this.TraceInformation("Emit {0}: {1}", lceventName, msg.MessageText);
                    this.Send(msg);
                    break;
            }
        }

        /// <summary>
        /// <para>Asynchronously sends payload using eventName</para>
        /// <para>payload must a string or Json Serializable</para>
        /// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
        /// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="payload">must be a string or a Json Serializable object</param>
        public void Emit(string eventName, dynamic payload)
        {
            this.Emit(eventName, payload, string.Empty, null);
        }

        public void Emit(string eventName)
        {
            this.Emit(eventName, null, string.Empty, null);
        }

        /// <summary>
        /// Queue outbound message
        /// </summary>
        /// <param name="msg"></param>
        public void Send(IMessage msg)
        {
            this.MessageQueueEmptyEvent.Reset();
            if (this.outboundQueue != null)
                this.outboundQueue.Add(msg.Encoded);
        }
        private void Send(string rawEncodedMessageText)
        {
            this.MessageQueueEmptyEvent.Reset();
            if (this.outboundQueue != null)
                this.outboundQueue.Add(rawEncodedMessageText);
        }

        /// <summary>
        /// if a registerd event name is found, don't raise the more generic Message event
        /// </summary>
        /// <param name="msg"></param>
        protected void OnMessageEvent(IMessage msg)
        {
            this.TraceInformation("OnMessageEvent: {0}", msg.MessageType.ToString());
            bool skip = false;
            if (!string.IsNullOrEmpty(msg.Event))
                skip = this.registrationManager.InvokeOnEvent(msg); // 

            var handler = this.Message;
            if (handler != null && !skip)
            {
                this.TraceInformation("webSocket_OnMessage: {0}", msg.RawMessage);
                handler(this, new MessageEventArgs(msg));
            }
        }

        /// <summary>
        /// Close SocketIO4Net.Client and clear all event registrations 
        /// </summary>
        public void Close()
        {
            this.retryConnectionCount = 0; // reset for next connection cycle
            // stop the heartbeat time
            this.CloseHeartBeatTimer();

            // stop outbound messages
            this.CloseOutboundQueue();

            this.CloseWebSocketClient();

            if (this.registrationManager != null)
            {
                this.registrationManager.Dispose();
                this.registrationManager = null;
            }

        }

        protected void CloseHeartBeatTimer()
        {
            // stop the heartbeat timer
            if (this.socketHeartBeatTimer != null)
            {
                this.socketHeartBeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                this.socketHeartBeatTimer.Dispose();
                this.socketHeartBeatTimer = null;
            }
        }
        protected void CloseOutboundQueue()
        {
            // stop outbound messages
            if (this.outboundQueue != null)
            {
                var queue = this.outboundQueue;
                this.outboundQueue.CompleteAdding(); // stop adding any more items;
                this.dequeuOutBoundMsgTask.Wait(700); // wait for dequeue thread to stop
                this.outboundQueue.Dispose();
                this.outboundQueue = null;
            }
        }
        protected void CloseWebSocketClient()
        {
            if (this.ioTransport != null)
            {
                DetachTransportEvents(this.ioTransport);

                if (this.ioTransport.State == WebSocketState.Connecting || this.ioTransport.State == WebSocketState.Open)
                {
                    try { this.ioTransport.Close(); }
                    catch { this.TraceError("exception raised trying to close websocket: can safely ignore, socket is being closed"); }
                }
                this.ioTransport = null;
            }
        }

        // websocket client events - open, messages, errors, closing
        private void WsClientOpenEvent(object sender, EventArgs e)
        {
            this.socketHeartBeatTimer = new Timer(OnHeartBeatTimerCallback, new object(), HandShake.HeartbeatInterval, HandShake.HeartbeatInterval);
            this.ConnectionOpenEvent.Set();

            this.OnMessageEvent(new EventMessage() { Event = "open" });
            if (this.Opened != null)
            {
                try { this.Opened(this, EventArgs.Empty); }
                catch (Exception ex) { this.TraceError("WsClientOpenEvent Error: {0}", ex.Message); }
            }

        }

        /// <summary>
        /// Raw websocket messages from server - convert to message types and call subscribers of events and/or callbacks
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void WsClientMessageReceived(object sender, TransportReceivedEventArgs e)
        {
            this.TraceInformation("WsClientMessageReceived: {0}", e.Message);

            IMessage iMsg = SocketIOClient.Messages.Message.Factory(e.Message);

            this.TraceInformation("iMessage: {0}", iMsg.MessageType.ToString());

            if (iMsg.Event == "responseMsg")
                this.TraceInformation("InvokeOnEvent: {0}", iMsg.RawMessage);

            switch (iMsg.MessageType)
            {
                case SocketIOMessageTypes.Disconnect:
                    if (string.IsNullOrWhiteSpace(iMsg.Endpoint)) // Disconnect the whole socket
                    {
                        this.Close();
                        this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
                    }
                    this.OnMessageEvent(iMsg);
                    break;
                case SocketIOMessageTypes.Heartbeat:
                    this.OnHeartBeatTimerCallback(null);
                    break;
                case SocketIOMessageTypes.Connect:
                case SocketIOMessageTypes.Message:
                case SocketIOMessageTypes.JSONMessage:
                case SocketIOMessageTypes.Event:
                case SocketIOMessageTypes.Error:
                    this.OnMessageEvent(iMsg);
                    break;
                case SocketIOMessageTypes.ACK:
                    this.registrationManager.InvokeCallBack(iMsg.AckId, iMsg.Json);
                    break;
                case SocketIOMessageTypes.Noop:
                    break;
                default:
                    this.TraceInformation("unknown wsClient message Received...");
                    break;
            }
        }

        /// <summary>
        /// websocket has closed unexpectedly - retry connection
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void WsClientClosed(object sender, EventArgs e)
        {
            this.TraceWarning("WsClientClosed");
            if (this.retryConnectionCount < this.RetryConnectionAttempts)
            {
                this.TraceInformation("Try reconnect...");
                this.ConnectionOpenEvent.Reset();
                this.ReConnect();
            }
            else
            {
                this.TraceWarning("No more retries");
                this.DetachTransportEvents(this.ioTransport);
                this.Close();
                this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
            }
        }

        private void WsClientError(object sender, ErrorEventArgs e)
        {
            this.OnErrorEvent(sender, new ErrorEventArgs("SocketClient error", e.Exception));
        }

        protected void OnErrorEvent(object sender, ErrorEventArgs e)
        {
            this.LastErrorMessage = e.Message;
            if (this.Error != null)
            {
                try { this.Error.Invoke(this, e); }
                catch { }
            }
            this.TraceError(string.Format("WsClientError: {0}\r\n\t{1}", e.Message, e.Exception));
        }
        protected void OnSocketConnectionClosedEvent(object sender, EventArgs e)
        {
            this.DeInit();
            this.TraceInformation("SocketConnectionClosedEvent");
            if (this.SocketConnectionClosed != null)
            {
                try { this.SocketConnectionClosed(sender, e); }
                catch { }
            }
        }
        protected void OnConnectionRetryAttemptEvent(object sender, EventArgs e)
        {
            if (this.ConnectionRetryAttempt != null)
            {
                try { this.ConnectionRetryAttempt(sender, e); }
                catch (Exception ex) { Trace.WriteLine(ex); }
            }
            this.TraceInformation(string.Format("Attempting to reconnect: {0}", this.retryConnectionCount));
        }

        // Housekeeping
        protected void OnHeartBeatTimerCallback(object state)
        {
            if (this.ReadyState == WebSocketState.Open)
            {
                IMessage msg = new Heartbeat();
                try
                {
                    if (this.outboundQueue != null && !this.outboundQueue.IsAddingCompleted)
                    {
                        this.outboundQueue.Add(msg.Encoded);
                        if (this.HeartBeatTimerEvent != null)
                        {
                            this.HeartBeatTimerEvent.BeginInvoke(this, EventArgs.Empty, EndAsyncEvent, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 
                    this.TraceError(string.Format("OnHeartBeatTimerCallback Error Event: {0}\r\n\t{1}", ex.Message, ex.InnerException));
                }
            }
        }
        private void EndAsyncEvent(IAsyncResult result)
        {
            var ar = (System.Runtime.Remoting.Messaging.AsyncResult)result;
            var invokedMethod = (EventHandler)ar.AsyncDelegate;

            try
            {
                invokedMethod.EndInvoke(result);
            }
            catch
            {
                // Handle any exceptions that were thrown by the invoked method
                this.TraceError("An event listener went kaboom!");
            }
        }
        /// <summary>
        /// While connection is open, dequeue and send messages to the socket server
        /// </summary>
        protected void DequeuOutboundMessages()
        {
            while (this.outboundQueue != null && !this.outboundQueue.IsAddingCompleted)
            {
                if (this.ReadyState == WebSocketState.Open)
                {
                    string msgString;
                    try
                    {
                        // will loop every 500ms if nothing to take
                        if (this.outboundQueue.TryTake(out msgString, 500))
                        {
                            //Trace.WriteLine(string.Format("webSocket_Send: {0}", msgString));
                            this.ioTransport.Send(msgString);
                        }
                        else
                            this.MessageQueueEmptyEvent.Set();
                    }
                    catch (Exception ex)
                    {
                        this.TraceError("The outboundQueue is no longer open...");
                    }
                }
                else
                {
                    this.ConnectionOpenEvent.WaitOne(2000); // wait for connection event
                }
            }
        }


#if NET40

        /// <summary>
        /// <para>Client performs an initial HTTP POST to obtain a SessionId (sid) assigned to a client, followed
        ///  by the heartbeat timeout, connection closing timeout, and the list of supported transports.</para>
        /// <para>The transport and sid are required as part of the ws: | wss: connection</para>
        /// </summary>
        /// <param name="uri">http://localhost:3000</param>
        /// <returns>Handshake object with sid value</returns>
        /// <example>DownloadString: 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling</example>
        protected Task RequestHandshake(Uri uri)
        {
            this.TraceInformation("RequestHandshake");
            string errorText = string.Empty;

            try
            {
                return Helpers.DownloadTasks.DownloadString(
                    string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port, uri.Query))
                    // #5 tkiley: The uri.Query is available in socket.io's handshakeData object during authorization
                .ContinueWith(
                    task =>
                        {
                            task.ThrowIfFailed();
                            string value = task.Result;
                            this.TraceInformation("Handshake Success: " + value);
                            // 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling
                            if (string.IsNullOrEmpty(value)) errorText = "Did not receive handshake from server";

                            if (string.IsNullOrEmpty(errorText)) this.HandShake.UpdateFromSocketIOResponse(value);
                            else this.HandShake.ErrorMessage = errorText;
                        });
            }
            #region Catch Exceptions
            catch (WebException webEx)
            {
                this.TraceError(string.Format("Handshake threw an exception...{0}", webEx.Message));
                switch (webEx.Status)
                {
                    case WebExceptionStatus.ConnectFailure:
                        errorText = string.Format("Unable to contact the server: {0}", webEx.Status);
                        break;
                    case WebExceptionStatus.NameResolutionFailure:
                        errorText = string.Format("Unable to resolve address: {0}", webEx.Status);
                        break;
                    case WebExceptionStatus.ProtocolError:
                        var resp = webEx.Response as HttpWebResponse; //((System.Net.HttpWebResponse)(webEx.Response))
                        if (resp != null)
                        {
                            switch (resp.StatusCode)
                            {
                                case HttpStatusCode.Forbidden:
                                    errorText = "Socket.IO Handshake Authorization failed";
                                    break;
                                default:
                                    errorText = string.Format("Handshake response status code: {0}", resp.StatusCode);
                                    break;
                            }
                        }
                        else
                            errorText = string.Format(
                                "Error getting handshake from Socket.IO host instance: {0}",
                                webEx.Message);
                        break;
                    default:
                        errorText = string.Format("Handshake threw an exception...{0}", webEx.Message);
                        break;
                }
            }
            catch (Exception ex)
            {
                errorText = string.Format("Error getting handshake from Socket.IO host instance: {0}", ex.Message);
                //this.OnErrorEvent(this, new ErrorEventArgs(errMsg));
            }
            return Task.Factory.StartNew(() => {});

            #endregion
        }

#endif


#if NET45
        /// <summary>
        /// <para>Client performs an initial HTTP POST to obtain a SessionId (sid) assigned to a client, followed
        ///  by the heartbeat timeout, connection closing timeout, and the list of supported transports.</para>
        /// <para>The transport and sid are required as part of the ws: | wss: connection</para>
        /// </summary>
        /// <param name="uri">http://localhost:3000</param>
        /// <returns>Handshake object with sid value</returns>
        /// <example>DownloadString: 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling</example>
        protected async Task RequestHandshake(Uri uri)
        {
            string value = string.Empty;
            string errorText = string.Empty;

            using (WebClient client = new WebClient())
            {
                try
                {
                    value = await client.DownloadStringTaskAsync(string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port, uri.Query)); // #5 tkiley: The uri.Query is available in socket.io's handshakeData object during authorization
                    // 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling
                    if (string.IsNullOrEmpty(value))
                        errorText = "Did not receive handshake from server";
                }
                #region Catch Exceptions
                catch (WebException webEx)
                {
                    Trace.WriteLine(string.Format("Handshake threw an exception...{0}", webEx.Message));
                    switch (webEx.Status)
                    {
                        case WebExceptionStatus.ConnectFailure:
                            errorText = string.Format("Unable to contact the server: {0}", webEx.Status);
                            break;
                        case WebExceptionStatus.NameResolutionFailure:
                            errorText = string.Format("Unable to resolve address: {0}", webEx.Status);
                            break;
                        case WebExceptionStatus.ProtocolError:
                            var resp = webEx.Response as HttpWebResponse;//((System.Net.HttpWebResponse)(webEx.Response))
                            if (resp != null)
                            {
                                switch (resp.StatusCode)
                                {
                                    case HttpStatusCode.Forbidden:
                                        errorText = "Socket.IO Handshake Authorization failed";
                                        break;
                                    default:
                                        errorText = string.Format("Handshake response status code: {0}", resp.StatusCode);
                                        break;
                                }
                            }
                            else
                                errorText = string.Format("Error getting handshake from Socket.IO host instance: {0}", webEx.Message);
                            break;
                        default:
                            errorText = string.Format("Handshake threw an exception...{0}", webEx.Message);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    errorText = string.Format("Error getting handshake from Socket.IO host instance: {0}", ex.Message);
                    //this.OnErrorEvent(this, new ErrorEventArgs(errMsg));
                }
                #endregion
            }
            if (string.IsNullOrEmpty(errorText))
                this.HandShake.UpdateFromSocketIOResponse(value);
            else
                this.HandShake.ErrorMessage = errorText;
        }
#endif

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // The bulk of the clean-up code 
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // free managed resources
                this.Close();
                this.MessageQueueEmptyEvent.Dispose();
                this.ConnectionOpenEvent.Dispose();
            }

        }


        private void TraceInformation(string format, params object[] args)
        {
            if (this.Debug) Trace.TraceInformation(format, args);
        }

        private void TraceWarning(string format, params object[] args)
        {
            if (this.Debug) Trace.TraceWarning(format, args);
        }

        private void TraceError(string format, params object[] args)
        {
            if (this.Debug) Trace.TraceError(format, args);
        }
    }
}
