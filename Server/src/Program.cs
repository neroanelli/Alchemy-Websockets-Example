/*
 * Copyright 2011 Olivine Labs, LLC. <http://olivinelabs.com>
 * Licensed under the MIT license: <http://www.opensource.org/licenses/mit-license.php>
 */

/*
 * Includes a reference to the Json.NET library <http://json.codeplex.com>, used under 
 * MIT license. See <http://www.opensource.org/licenses/mit-license.php>  for license details. 
 * Json.NET is copyright 2007 James Newton-King
 */

/* 
 * Includes the Alchemy Websockets Library 
 * <http://www.olivinelabs.com/index.php/projects/71-alchemy-websockets>, 
 * used under LGPL license. See <http://www.gnu.org/licenses/> for license details. 
 * Alchemy Websockets is copyright 2011 Olivine Labs.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Alchemy;
using Alchemy.Classes;
using Newtonsoft.Json;
using System.Net;
using System.Threading;

namespace ChatServer
{
    class Program
    {
        /// <summary>
        /// Store the list of online users. Wish I had a ConcurrentList. 
        /// </summary>
        protected static ConcurrentDictionary<User, string> OnlineUsers = new ConcurrentDictionary<User, string>();

        /// <summary>
        /// Initialize the application and start the Alchemy Websockets server
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            // Initialize the server on port 81, accept any IPs, and bind events.
            var aServer = new WebSocketServer(82, IPAddress.Any)
                              {
                                  OnReceive = OnReceive,
                                  OnSend = OnSend,
                                  OnConnected = OnConnect,
                                  OnDisconnect = OnDisconnect,
                                  TimeOut = new TimeSpan(0, 5, 0)
                              };

            aServer.Start();

            // Accept commands on the console and keep it alive
            var command = string.Empty;
            while (command != "exit")
            {
                command = Console.ReadLine();
            }

            aServer.Stop();
            Polled = false;
        }
        public static bool Polled = false;

        /// <summary>
        /// Event fired when a client connects to the Alchemy Websockets server instance.
        /// Adds the client to the online users list.
        /// </summary>
        /// <param name="context">The user's connection context</param>
        public static void OnConnect(UserContext context)//客户端连接，将客户端加入用户列表
        {
            Console.WriteLine("Client Connection From : " + context.ClientAddress);

            var me = new User {Context = context};

            OnlineUsers.TryAdd(me, String.Empty);
        }

        /// <summary>
        /// Event fired when a data is received from the Alchemy Websockets server instance.
        /// Parses data as JSON and calls the appropriate message or sends an error message.
        /// </summary>
        /// <param name="context">The user's connection context</param>
        public static void OnReceive(UserContext context)
        {
            Console.WriteLine("Received Data From :" + context.ClientAddress);

            try
            {
                var json = context.DataFrame.ToString();

                // <3 dynamics
                dynamic obj = JsonConvert.DeserializeObject(json);

                switch ((int)obj.Type)
                {
                    case (int)CommandType.Register:
                        Register(obj.Name.Value, context);
                        break;
                    case (int)CommandType.Message:
                        ChatMessage(obj.Message.Value, context);
                        break;
                    case (int)CommandType.NameChange:
                        NameChange(obj.Name.Value, context);
                        break;
                    case (int)CommandType.Poll:
                        if (!Polled)
                        {
                            PollData(context);
                        }
                        break;    
                }
            }
            catch (Exception e) // Bad JSON! For shame.
            {
                var r = new Response {Type = ResponseType.Error, Data = new {e.Message}};

                context.Send(JsonConvert.SerializeObject(r));
            }
        }

        /// <summary>
        /// Event fired when the Alchemy Websockets server instance sends data to a client.
        /// Logs the data to the console and performs no further action.
        /// </summary>
        /// <param name="context">The user's connection context</param>
        public static void OnSend(UserContext context)
        {
            Console.WriteLine("Data Send To : " + context.ClientAddress);
        }

        /// <summary>
        /// Event fired when a client disconnects from the Alchemy Websockets server instance.
        /// Removes the user from the online users list and broadcasts the disconnection message
        /// to all connected users.
        /// </summary>
        /// <param name="context">The user's connection context</param>
        public static void OnDisconnect(UserContext context)
        {
            Console.WriteLine("Client Disconnected : " + context.ClientAddress);
            var user = OnlineUsers.Keys.Where(o => o.Context.ClientAddress == context.ClientAddress).Single();

            string trash; // Concurrent dictionaries make things weird

            OnlineUsers.TryRemove(user, out trash);

            if (!String.IsNullOrEmpty(user.Name))
            {
                var r = new Response {Type = ResponseType.Disconnect, Data = new {user.Name}};

                Broadcast(JsonConvert.SerializeObject(r));
            }

            BroadcastNameList();
            Polled = false;
        }

        /// <summary>
        /// Register a user's context for the first time with a username, and add it to the list of online users
        /// </summary>
        /// <param name="name">The name to register the user under</param>
        /// <param name="context">The user's connection context</param>
        private static void Register(string name, UserContext context)
        {
            var u = OnlineUsers.Keys.Where(o => o.Context.ClientAddress == context.ClientAddress).Single();
            var r = new Response();

            if (ValidateName(name)) {
                u.Name = name;

                r.Type = ResponseType.Connection;
                r.Data = new { u.Name };

                Broadcast(JsonConvert.SerializeObject(r));

                BroadcastNameList();
                OnlineUsers[u] = name;
            }
            else
            {
               SendError("Name is of incorrect length.", context);
            }
        }

        /// <summary>
        /// Broadcasts a chat message to all online usrs
        /// </summary>
        /// <param name="message">The chat message to be broadcasted</param>
        /// <param name="context">The user's connection context</param>
        private static void ChatMessage(string message, UserContext context)
        {
            var u = OnlineUsers.Keys.Where(o => o.Context.ClientAddress == context.ClientAddress).Single();
            var r = new Response {Type = ResponseType.Message, Data = new {u.Name, Message = message}};
            //while (true)
            //{
                Broadcast(JsonConvert.SerializeObject(r),null);
                //Thread.Sleep(500);
           // }
        }

        /// <summary>
        /// Update a user's name if they sent a name-change command from the client.
        /// </summary>
        /// <param name="name">The name to be changed to</param>
        /// <param name="aContext">The user's connection context</param>
        private static void NameChange(string name, UserContext aContext)
        {
            var u = OnlineUsers.Keys.Where(o => o.Context.ClientAddress == aContext.ClientAddress).Single();

            if (ValidateName(name)) { 
                var r = new Response
                                 {
                                     Type = ResponseType.NameChange,
                                     Data = new {Message = u.Name + " is now known as " + name}
                                 };
                Broadcast(JsonConvert.SerializeObject(r));

                u.Name = name;
                OnlineUsers[u] = name;

                BroadcastNameList();
            }
            else
            {
                SendError("Name is of incorrect length.", aContext);
            }
        }
        private static void PollData(UserContext context)
        {
            Polled = true;
            float f1 = 0.0f;
            float f2=1.1f;
            OpcData[] OPC = new OpcData[100];
            string[] test = new string[100];
            //while (true)
            //{
            //    foreach (var u in OnlineUsers.Keys)
            //    {
            //        u.Context.Send(r.ToString());
            //        Console.WriteLine("Data Send To : " +r.ToString());
            //    }
            //    Thread.Sleep(500);
            //    r=r+1.1f;
            var p1 = new OpcData
            {

                TagName = "Tag1",
                Name = "水温度",
                DataType = OpcDataType.FloatDataItem,
                Value = f1
            };
            var p2 = new OpcData
            {
                TagName = "Tag2",
                Name = "水压力",
                DataType = OpcDataType.FloatDataItem,
                Value = f2

            };
            for (int i = 0; i < 100;i++ )
            {
                    OPC[i] = new OpcData 
                 {
                    TagName="Tag"+i.ToString(),
                    Name="",
                    DataType=OpcDataType.FloatDataItem,
                    Value=i
                 };
                    test[i] = i.ToString() + "testeste";
                    //var a=System.Runtime.InteropServices.Marshal.SizeOf(test);
            }


           // var r = new {OPC };
            while (Polled)
            {
               
               // var r = new[] {};
                foreach (var u in OnlineUsers.Keys)
                {
                    u.Context.Send(JsonConvert.SerializeObject(OPC));
                    Console.WriteLine("Data Send To : " + OPC);
                }
                //context.Send(JsonConvert.SerializeObject(r));
                Thread.Sleep(500);
               f1=f1+2.312f;

            }
        }
        /// <summary>
        /// Broadcasts an error message to the client who caused the error
        /// </summary>
        /// <param name="errorMessage">Details of the error</param>
        /// <param name="context">The user's connection context</param>
        private static void SendError(string errorMessage, UserContext context)
        {
            var r = new Response {Type = ResponseType.Error, Data = new {Message = errorMessage}};

            context.Send(JsonConvert.SerializeObject(r));
        }

        /// <summary>
        /// Broadcasts a list of all online users to all online users
        /// </summary>
        private static void BroadcastNameList()
        {
            var r = new Response
                        {
                            Type = ResponseType.UserCount,
                            Data = new {Users = OnlineUsers.Values.Where(o => !String.IsNullOrEmpty(o)).ToArray()}
                        };
            Broadcast(JsonConvert.SerializeObject(r));
        }

        /// <summary>
        /// Broadcasts a message to all users, or if users is populated, a select list of users
        /// </summary>
        /// <param name="message">Message to be broadcast</param>
        /// <param name="users">Optional list of users to broadcast to. If null, broadcasts to all. Defaults to null.</param>
        private static void Broadcast(string message, ICollection<User> users = null)//广播信息
        {
            if (users == null)
            {
                foreach (var u in OnlineUsers.Keys)
                {
                    u.Context.Send(message);
                }
            }
            else
            {
                foreach (var u in OnlineUsers.Keys.Where(users.Contains))
                {
                    u.Context.Send(message);
                }
            }
        }

        /// <summary>
        /// Checks validity of a user's name
        /// </summary>
        /// <param name="name">Name to check</param>
        /// <returns></returns>
        private static bool ValidateName(string name)//验证用户名是否有效
        {
            var isValid = false;
            if (name.Length > 3 && name.Length < 25)
            {
                isValid = true;
            }

            return isValid;
        }

        /// <summary>
        /// Defines the type of response to send back to the client for parsing logic
        /// </summary>
        public enum ResponseType//定义枚举数据，应答类型
        {
            Connection = 0,
            Disconnect = 1,
            Message = 2,
            NameChange = 3,
            UserCount = 4,
            Error = 255
        }

        /// <summary>
        /// Defines the response object to send back to the client
        /// </summary>
        public class Response//定义对客户端发送的应答信息
        {
            public ResponseType Type { get; set; }
            public dynamic Data { get; set; }
        }

        public class OpcData
        {
            public string TagName;
            public string Name;
            public OpcDataType DataType;
            public dynamic Value;
        }
        /// <summary>
        /// Holds the name and context instance for an online user
        /// </summary>
        public class User //
        {
            public string Name = String.Empty;
            public UserContext Context { get; set; }
        }

        /// <summary>
        /// Defines a type of command that the client sends to the server
        /// </summary>
        public enum CommandType //枚举数据，客户端发送命令类型
        {
            Register = 0,
            Message,
            NameChange,
            Poll
        }
         public enum OpcDataType //枚举数据，OPC数据类型
        {
            BoolDataItem = 0,
            IntDataItem,
            FloatDataItem,
            StringDataItem
        }
    }
}
