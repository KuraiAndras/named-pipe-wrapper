﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using NamedPipeWrapperStandard;
using NUnit.Framework;

namespace NamedPipeWrapper.UnitTests
{
    [TestFixture]
    internal class SerializableTests
    {
        private static readonly ILog Logger =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static SerializableTests()
        {
            var layout = new PatternLayout("%-6timestamp %-5level - %message%newline");
            var appender = new ConsoleAppender { Layout = layout };
            layout.ActivateOptions();
            appender.ActivateOptions();

            BasicConfigurator.Configure(
                LogManager.GetRepository(Assembly.GetAssembly(typeof(SerializableTests))),
                appender);
        }

        private const string PipeName = "data_test_pipe";

        private NamedPipeServer<TestCollection>? _server;
        private NamedPipeClient<TestCollection>? _client;

        private TestCollection? _expectedData;
        private int _expectedHash;
        private TestCollection? _actualData;
        private int _actualHash;
        private bool _clientDisconnected;

        private DateTime _startTime;

        private readonly ManualResetEvent _barrier = new ManualResetEvent(false);

        private readonly IList<Exception> _exceptions = new List<Exception>();

        #region Setup and teardown

        [SetUp]
        public void SetUp()
        {
            Logger.Debug("Setting up test...");

            _barrier.Reset();
            _exceptions.Clear();

            _server = new NamedPipeServer<TestCollection>(PipeName);
            _client = new NamedPipeClient<TestCollection>(PipeName);

            _expectedData = null;
            _expectedHash = 0;
            _actualData = null;
            _actualHash = 0;
            _clientDisconnected = false;

            _server.ClientMessage += ServerOnClientMessage;

            _server.Error += OnError;
            _client.Error += OnError;

            _server.Start();
            _client.Start();

            // Give the client and server a few seconds to connect before sending data
            Thread.Sleep(TimeSpan.FromSeconds(1));

            Logger.Debug("Client and server started");
            Logger.Debug("---");

            _startTime = DateTime.Now;
        }

        private void OnError(Exception exception)
        {
            _exceptions.Add(exception);
            _barrier.Set();
        }

        [TearDown]
        public void TearDown()
        {
            Logger.Debug("---");
            Logger.Debug("Stopping client and server...");

            _server.ClientMessage -= ServerOnClientMessage;

            _server.Stop();
            _client.Stop();

            Logger.Debug("Client and server stopped");
            Logger.DebugFormat("Test took {0}", (DateTime.Now - _startTime));
            Logger.Debug("~~~~~~~~~~~~~~~~~~~~~~~~~~");
        }

        #endregion

        #region Events

        private void ServerOnClientMessage(NamedPipeConnection<TestCollection, TestCollection> connection, TestCollection message)
        {
            Logger.DebugFormat("Received collection with {0} items from the client", message.Count);
            _actualData = message;
            _actualHash = message.GetHashCode();
            _barrier.Set();
        }

        #endregion

        #region Tests

        [Test]
        public void TestCircularReferences()
        {
            _expectedData = new TestCollection();
            for (var i = 0; i < 10; i++)
            {
                var item = new TestItem(i, _expectedData, RandomEnum());
                _expectedData.Add(item);
            }
            _expectedHash = _expectedData.GetHashCode();

            _client.PushMessage(_expectedData);
            _barrier.WaitOne(TimeSpan.FromSeconds(5));

            if (_exceptions.Any())
                throw new AggregateException(_exceptions);

            Assert.NotNull(_actualHash, $"Server should have received client's {_expectedData.Count} item message");
            Assert.AreEqual(_expectedHash, _actualHash,
                $"Hash codes for {_expectedData.Count} item message should match");
            Assert.AreEqual(_expectedData.Count, _actualData.Count, "Collection lengths should be equal");

            for (var i = 0; i < _actualData.Count; i++)
            {
                var expectedItem = _expectedData[i];
                var actualItem = _actualData[i];
                Assert.AreEqual(expectedItem, actualItem, $"Items at index {i} should be equal");
                Assert.AreEqual(actualItem.Parent, _actualData,
                    $"Item at index {i}'s Parent property should reference the item's parent collection");
            }
        }

        private TestEnum RandomEnum()
        {
            var rand = new Random().NextDouble();
            if (rand < 0.33)
                return TestEnum.A;
            if (rand < 0.66)
                return TestEnum.B;
            return TestEnum.C;
        }

        #endregion
    }

    [Serializable]
    internal class TestCollection : List<TestItem>
    {
        public override int GetHashCode()
        {
            var strs = new List<string>(Count);
            foreach (var item in ToArray())
            {
                strs.Add(item.GetHashCode().ToString());
            }
            var str = string.Join(",", strs);
            return Hash(Encoding.UTF8.GetBytes(str)).GetHashCode();
        }

        /// <summary>
        /// Computes the SHA-1 hash (lowercase) of the specified byte array.
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private static string Hash(byte[] bytes)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha1.ComputeHash(bytes);
                var sb = new StringBuilder();
                for (var i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }

    [Serializable]
    internal class TestItem
    {
        public readonly int Id;
        public readonly TestCollection Parent;
        public readonly TestEnum Enum;

        public TestItem(int id, TestCollection parent, TestEnum @enum)
        {
            Id = id;
            Parent = parent;
            Enum = @enum;
        }

        protected bool Equals(TestItem other) => Id == other.Id && Enum == other.Enum;

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((TestItem)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Id * 397) ^ (int)Enum;
            }
        }
    }

    internal enum TestEnum
    {
        A = 1,
        B = 2,
        C = 3,
    }
}
