﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using System.Threading.Tasks;

namespace NeoCortexApi.DistributedComputeLib
{
    public abstract class AkkaDistributedDictionaryBase<TKey, TValue> : IDictionary<TKey, TValue>, IEnumerator<KeyValuePair<TKey, TValue>>
    {

        protected AkkaDistributedDictConfig Config { get; }

        private Dictionary<TKey, TValue>[] dictList;

        private IActorRef[] dictActors;

        private int numElements = 0;

        private ActorSystem actSystem;

        public AkkaDistributedDictionaryBase(AkkaDistributedDictConfig config)
        {
            if (config == null)
                throw new ArgumentException("Configuration must be specified.");

            this.Config = config;

            dictActors = new IActorRef[config.Nodes.Count];

            actSystem = ActorSystem.Create("Deployer", ConfigurationFactory.ParseString(@"
                akka {  
                    actor{
                        provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""  		               
                    }
                    remote {
                        helios.tcp {
		                    port = 0
		                    hostname = localhost
                        }
                    }
                }"));

            int nodeIndx = 0;

            foreach (var node in this.Config.Nodes)
            {
                dictActors[nodeIndx] =
                  actSystem.ActorOf(Props.Create(() => new DictNodeActor())
                  .WithDeploy(Deploy.None.WithScope(new RemoteScope(Address.Parse(node)))), $"{nameof(DictNodeActor)}-{nodeIndx}");

                //dictActors[nodeIndx] =
                //  actSystem.ActorOf(Props.Create(() => new DictNodeActor<TKey, TValue>())
                //  .WithDeploy(Deploy.None.WithScope(new RemoteScope(Address.Parse(node)))), $"{nameof(DictNodeActor<TKey,TValue>)}-{nodeIndx}");

                var result = dictActors[nodeIndx].Ask<int>(new CreateDictNodeMsg(), this.Config.ConnectionTimout).Result;


                //result = dictActors[nodeIndx].Ask<int>("abc", this.Config.ConnectionTimout).Result;

                //result = dictActors[nodeIndx].Ask<int>(new CreateDictNodeMsg(), this.Config.ConnectionTimout).Result;

            }


        }

        /// <summary>
        /// Depending on usage (Key type) different mechanism can be used to partition keys.
        /// This method returns the index of the node, whish should hold specified key.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected abstract int GetPartitionNodeIndexFromKey(TKey key);

        public TValue this[TKey key]
        {
            get
            {
                var nodeIndex = GetPartitionNodeIndexFromKey(key);
                TValue val = this.dictActors[nodeIndex].Ask<TValue>("").Result;
                return val;
            }
            set
            {
                var nodeIndex = GetPartitionNodeIndexFromKey(key);

                var isSet = dictActors[nodeIndex].Ask<int>(new AddElementMsg()
                {
                    Elements = new List<KeyPair>
                    {
                        new KeyPair { Key=key, Value=value }
                    }

                }, this.Config.ConnectionTimout).Result;

                if (isSet != 1)
                    throw new ArgumentException("Cannot find the element with specified key!");
            }
        }


        public ICollection<TKey> Keys
        {
            get
            {
                List<TKey> keys = new List<TKey>();
                foreach (var item in this.dictList)
                {
                    foreach (var k in item.Keys)
                    {
                        keys.Add(k);
                    }
                }

                return keys;
            }
        }


        public ICollection<TValue> Values
        {
            get
            {
                List<TValue> keys = new List<TValue>();
                foreach (var item in this.dictList)
                {
                    foreach (var k in item.Values)
                    {
                        keys.Add(k);
                    }
                }

                return keys;
            }
        }


        public int Count
        {
            get
            {
                int cnt = 0;

                foreach (var item in this.dictList)
                {
                    cnt += item.Values.Count;
                }

                return cnt;
            }
        }

        public bool IsReadOnly => false;

        /// <summary>
        /// Ads the value with secified key to the right parition.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Add(TKey key, TValue value)
        {
            var nodeIndex = GetPartitionNodeIndexFromKey(key);

            var isSet = dictActors[nodeIndex].Ask<int>(new AddElementMsg()
            {
                Elements = new List<KeyPair>
                    {
                        new KeyPair { Key=key, Value=value }
                    }

            }, this.Config.ConnectionTimout).Result;

            if (isSet != 1)
                throw new ArgumentException("Cannot add the element with specified key!");
        }

        /// <summary>
        /// Tries to return value from target partition.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            var nodeIndex = GetPartitionNodeIndexFromKey(key);

            Result result = dictActors[nodeIndex].Ask<Result>(new GetElementMsg { Key = key }).Result;

            if (result.IsError == false)
            {
                value = (TValue)result.Value;
                return true;
            }
            else
            {
                value = default(TValue);
                return false;
            }
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            int partitionId = GetPartitionNodeIndexFromKey(item.Key);
            this.dictList[partitionId].Add(item.Key, item.Value);
        }

        public void Clear()
        {
            foreach (var item in this.dictList)
            {
                item.Clear();
            }
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            int partitionId = GetPartitionNodeIndexFromKey(item.Key);

            if (ContainsKey(item.Key))
            {
                var val = this.dictActors[partitionId].Ask<TValue>(new GetElementMsg()).Result;
                if (EqualityComparer<TValue>.Default.Equals(val, item.Value))
                    return true;
                else
                    return false;
            }

            return false;
        }

        /// <summary>
        /// Checks if element with specified key exists in any partition in cluster.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool ContainsKey(TKey key)
        {
            int partitionId = GetPartitionNodeIndexFromKey(key);

            if (this.dictActors[partitionId].Ask<bool>(new ContainsKeyMsg { Key = key }).Result)
                return true;
            else
                return false;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public bool Remove(TKey key)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(key))
                {
                    return this.dictList[i].Remove(key);
                }
            }

            return false;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(item.Key))
                {
                    return this.dictList[i].Remove(item.Key);
                }
            }

            return false;
        }



        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        }

        #region Enumerators

        /// <summary>
        /// Current dictionary list in enemerator.
        /// </summary>
        private int currentDictIndex = -1;

        /// <summary>
        /// Current index in currentdictionary
        /// </summary>
        private int currentIndex = -1;

        public object Current => this.dictList[this.currentDictIndex].ElementAt(currentIndex);

        KeyValuePair<TKey, TValue> IEnumerator<KeyValuePair<TKey, TValue>>.Current => this.dictList[this.currentDictIndex].ElementAt(currentIndex);


        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return this;
        }


        public bool MoveNext()
        {
            if (this.currentIndex == -1)
                this.currentIndex = 0;

            if (this.currentDictIndex + 1 < this.dictList.Length)
            {
                this.currentDictIndex++;

                if (this.dictList[this.currentDictIndex].Count > 0 && this.dictList[this.currentDictIndex].Count > this.currentIndex)
                    return true;
                else
                    return false;
            }
            else
            {
                this.currentDictIndex = 0;

                if (this.currentIndex + 1 < this.dictList[this.currentDictIndex].Count)
                {
                    this.currentIndex++;
                    return true;
                }
                else
                    return false;
            }
        }


        public bool MoveNextOLD()
        {
            if (this.currentDictIndex == -1)
                this.currentDictIndex++;

            if (this.currentIndex + 1 < this.dictList[this.currentDictIndex].Count)
            {
                this.currentIndex++;
                return true;
            }
            else
            {
                if (this.currentDictIndex < this.dictList.Length)
                {
                    this.currentDictIndex++;

                    if (this.dictList[this.currentDictIndex].Count > 0)
                        return true;
                    else
                        return false;
                }
                else
                    return false;
            }
        }

        public void Reset()
        {
            this.currentDictIndex = -1;
            this.currentIndex = -1;
        }

        public void Dispose()
        {
            this.dictList = null;
        }
        #endregion
    }
}
