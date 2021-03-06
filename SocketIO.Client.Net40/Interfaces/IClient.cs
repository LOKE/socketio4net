﻿using System;
namespace SocketIOClient
{
	/// <summary>
	/// C# Socket.IO client interface
	/// </summary>
	public interface IClient
	{
		event EventHandler Opened;
		event EventHandler<MessageEventArgs> Message;
		event EventHandler SocketConnectionClosed;
        event EventHandler<ErrorEventArgs> ConnectionError;
        event EventHandler<ErrorEventArgs> Error;

		IOHandshake HandShake { get; }
        bool Debug { get; set; }
        bool IsConnected { get; }
        WebSocketState ReadyState { get; }
        int RetryConnectionAttempts { get; set; }

        void Connect();
		IEndPointClient Connect(string endPoint);

		void Close();
		void Dispose();

		void On(string eventName, Action<SocketIOClient.Messages.IMessage> action);
		void On(string eventName, string endPoint, Action<SocketIOClient.Messages.IMessage> action);

		void Emit(string eventName, dynamic payload);
		void Emit(string eventName, dynamic payload, string endPoint = "", Action<dynamic> callBack = null);
		
		void Send(SocketIOClient.Messages.IMessage msg);
		//void Send(string rawEncodedMessageText);
	}
}
