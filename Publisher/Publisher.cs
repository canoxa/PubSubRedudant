using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Runtime.Remoting.Messaging;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace PubSub
{
    public class Publisher
    {

        private string url;
        private string name;
        private string site;

        public string URL
        {
            get { return url; }
            set { url = value; }
        }
        public string Name
        {
            get { return name; }
            set { name = value; }
        }
        public string Site
        {
            get { return site; }
            set { site = value; }
        }

        

        public Publisher(string u, string n, string s)
        {
            URL = u;
            Name = n;
            Site = s;
        }

        static void Main(string[] args)
        {
            Console.WriteLine("@publisher !!! porto -> {0}", args[0]);

            string[] arguments = args[0].Split(';');//arguments[0]->port; arguments[1]->url; arguments[2]->nome; arguments[3]->site;arguments[4]->urlBroker
            List<string> urlBrokerList = new List<string>();

            for (int i = 4; i < arguments.Length; i++)
            {
                urlBrokerList.Add(arguments[i]);
            }
            TcpChannel channel = new TcpChannel(Int32.Parse(arguments[0]));
            ChannelServices.RegisterChannel(channel, true);

            MPMPubImplementation MPMpublish = new MPMPubImplementation(arguments[0], arguments[1], arguments[3], arguments[2],urlBrokerList);
            
            RemotingServices.Marshal(MPMpublish, "PMPublish", typeof(MPMPubImplementation));

            MPMPublisherCmd processCmd = new MPMPublisherCmd();
            RemotingServices.Marshal(processCmd, "MPMProcessCmd", typeof(MPMPublisherCmd));

            Console.ReadLine();
        }

    }
    class MPMPubImplementation : MarshalByRefObject, PubInterface
    {
        private string myPort;
        private string url;
        private string site;
        private string name;
        //private string urlMyBroker;
        private List<string> urlMyBroker;
        private int count;
        private Dictionary<string, int> topic_number;//#seq por topico

        // vida infinita !!!!
        public override object InitializeLifetimeService()
        {
            return null;
        }
        public delegate void PubRemoteAsyncDelegate(Message m, string pubName, int filter, int order, int eventNumber, int logMode);
        public static void PubRemoteAsyncCallBack(IAsyncResult ar)
        {
            PubRemoteAsyncDelegate del = (PubRemoteAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
            return;
        }

        public MPMPubImplementation(string p1, string p2, string p3, string p4,List<string> p5)
        {
            this.myPort = p1;
            this.url = p2;
            this.site = p3;
            this.name = p4;
            this.count = 1;
            this.urlMyBroker=p5;
            topic_number = new Dictionary<string, int>();

        }
        public void publish(string number, string topic, string secs,int filter, int order, int eventNumber,int logMode)
        {
            string urlRemote = url.Substring(0, url.Length - 8);//retirar XXXX/publisher
            string myURL = urlRemote + myPort;

            Console.WriteLine("@MPMPubImplementatio - {0} publishing events, on topic {1}", myURL, topic);

            if (!topic_number.ContainsKey(topic))
            {
                topic_number.Add(topic, Int32.Parse(number));
            }
            else {
                topic_number[topic] = topic_number[topic] + Int32.Parse(number);
                }

            
            for (int i = 0; i < Int32.Parse(number); i++)
            {
                Console.WriteLine("Publicar {0} seqNumber {1} modo {2}", topic, count,filter);
                Message maux = new Message(topic, i.ToString(), count, name);
                count++;

                LogInterface log = (LogInterface)Activator.GetObject(typeof(LogInterface), "tcp://localhost:8086/PuppetMasterLog");
                log.log(this.name, this.name, topic, eventNumber, "publisher");

                System.Threading.Thread.Sleep(Int32.Parse(secs));

                foreach (var broker in urlMyBroker)
                {
                    BrokerReceiveBroker pub = (BrokerReceiveBroker)Activator.GetObject(typeof(BrokerReceiveBroker), broker + "BrokerCommunication");
                    try
                    {
                        PubRemoteAsyncDelegate remoteDel = new PubRemoteAsyncDelegate(pub.receivePublication);
                        AsyncCallback RemoteCallBack = new AsyncCallback(PubRemoteAsyncCallBack);
                        IAsyncResult remAr = remoteDel.BeginInvoke(maux, this.name, filter, order, eventNumber, logMode, RemoteCallBack, null);

                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Could not locate server");
                    }
                    //pub.receivePublication(maux, this.name, filter, order, eventNumber, logMode);
                }
                
                eventNumber++;
            }

        }

        public void status()
        {
            Console.Write("I'm publisher {0} at url {1} and port {2} and I've published:\n", name, url, myPort);
            foreach (KeyValuePair<string, int> t in topic_number)
            {
                Console.WriteLine("\t" + t.Value + " event(s) on topic " + t.Key + "\r\n");
            }
        }
    }

    public class MPMPublisherCmd : MarshalByRefObject, IProcessCmd
    {

        public void crash()
        {
            // sledgehammer solution -> o mesmo que unplug
            Environment.Exit(1);
        }

        public void freeze()
        {
            throw new NotImplementedException();
        }

        public void unfreeze()
        {
            throw new NotImplementedException();
        }
    }
}
