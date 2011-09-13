﻿// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
// limitations under the License.
namespace CassandraSharp
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using Apache.Cassandra;
    using CassandraSharp.Config;
    using CassandraSharp.EndpointStrategy;
    using CassandraSharp.Pool;
    using CassandraSharp.Transport;
    using CassandraSharp.Utils;
    using Thrift.Protocol;
    using Thrift.Transport;

    internal class Cluster : ICluster
    {
        private readonly BehaviorConfig _config;

        private readonly IEndpointStrategy _endpointsManager;

        private readonly IPool<IConnection> _pool;

        private readonly ITransportFactory _transportFactory;

        public Cluster(BehaviorConfig config, IPool<IConnection> pool, ITransportFactory transportFactory, IEndpointStrategy endpointsManager)
        {
            _config = config;
            _pool = pool;
            _endpointsManager = endpointsManager;
            _transportFactory = transportFactory;
            DefaultReadConsistencyLevel = config.DefaultReadConsistencyLevel;
            DefaultWriteConsistencyLevel = config.DefaultWriteConsistencyLevel;
            DefaultKeyspace = config.DefaultKeyspace;
        }

        public ConsistencyLevel DefaultReadConsistencyLevel { get; private set; }

        public ConsistencyLevel DefaultWriteConsistencyLevel { get; private set; }

        public string DefaultKeyspace { get; private set; }

        public void Dispose()
        {
            _pool.SafeDispose();
        }

        public TResult Execute<TResult>(Func<Cassandra.Client, TResult> func)
        {
            int tryCount = 1;
            while (true)
            {
                IConnection connection = null;
                try
                {
                    connection = AcquireConnection();

                    OpenConnection(connection);
                    TResult res = func(connection.CassandraClient);

                    ReleaseConnection(connection, false);

                    return res;
                }
                catch (Exception ex)
                {
                    bool connectionDead;
                    bool retry;
                    DecipherException(ex, out connectionDead, out retry);

                    ReleaseConnection(connection, connectionDead);
                    if (!retry || tryCount >= _config.MaxRetries)
                    {
                        throw;
                    }
                }

                ++tryCount;
            }
        }

        private void OpenConnection(IConnection connection)
        {
            TTransport transport = connection.CassandraClient.InputProtocol.Transport;
            if (!transport.IsOpen)
            {
                transport.Open();
            }

            if (connection.KeySpace != DefaultKeyspace)
            {
                connection.CassandraClient.set_keyspace(DefaultKeyspace);
                connection.KeySpace = DefaultKeyspace;
            }
        }

        private void DecipherException(Exception ex, out bool connectionDead, out bool retry)
        {
            // connection dead exception handling
            if (ex is TTransportException)
            {
                connectionDead = true;
                retry = true;
            }
            else if (ex is IOException)
            {
                connectionDead = true;
                retry = true;
            }
            else if (ex is SocketException)
            {
                connectionDead = true;
                retry = true;
            }

                // functional exception handling
            else if (ex is TimedOutException)
            {
                connectionDead = false;
                retry = _config.RetryOnTimeout;
            }
            else if (ex is UnavailableException)
            {
                connectionDead = false;
                retry = _config.RetryOnUnavailable;
            }

                // other exceptions ==> connection is not dead / do not retry
            else
            {
                connectionDead = false;
                retry = false;
            }
        }

        private IConnection AcquireConnection()
        {
            IConnection connection;
            if (_pool.Acquire(out connection))
            {
                return connection;
            }

            TTransport transport = null;
            try
            {
                Endpoint endpoint = _endpointsManager.Pick();
                if (null == endpoint)
                {
                    throw new ArgumentException("Can't find any valid endpoint");
                }

                transport = _transportFactory.Create(endpoint.Address);
                TProtocol protocol = new TBinaryProtocol(transport);
                Cassandra.Client client = new Cassandra.Client(protocol);

                connection = new Connection(client, endpoint);
                return connection;
            }
            catch
            {
                if (null != transport)
                {
                    transport.Close();
                }
                throw;
            }
        }

        private void ReleaseConnection(IConnection connection, bool hasFailed)
        {
            // protects against exception during acquire connection
            if (null != connection)
            {
                if (hasFailed)
                {
                    _endpointsManager.Ban(connection.Endpoint);
                    connection.SafeDispose();
                }
                else
                {
                    _pool.Release(connection);
                }
            }
        }

        private class Connection : IConnection
        {
            public Connection(Cassandra.Client client, Endpoint endpoint)
            {
                Endpoint = endpoint;
                CassandraClient = client;
            }

            public void Dispose()
            {
                CassandraClient.InputProtocol.Transport.Close();
            }

            public string KeySpace { get; set; }

            public Endpoint Endpoint { get; private set; }

            public Cassandra.Client CassandraClient { get; private set; }
        }
    }
}