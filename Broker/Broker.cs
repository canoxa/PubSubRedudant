using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace PubSub
{

    public class Broker
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

        public Broker(string u, string n, string s)
        {
            URL = u;
            Name = n;
            Site = s;
        }

        static void Main(string[] args)
        {
           // Console.WriteLine("@broker !!! porto -> {0}", args[0]);

            string[] arguments = args[0].Split(';');//arguments[0]->port; arguments[1]->url; arguments[2]->nome; arguments[3]->site; arguments[4]->replicas;

            int vizinhos = arguments.Length - 6;//numero de vizinhos
            List<Broker> lstaux = new List<Broker>();
            //iniciar lista de vizinhos
            for (int i = 5; i < vizinhos + 5; i++) {
                string[] atr = arguments[i].Split('%');//atr[0]-name, atr[1]-site, atr[2]-url
                Broker b = new Broker(atr[2], atr[0], atr[1]);
                lstaux.Add(b);
            }

            string aux = arguments[4].Substring(0,arguments[4].Length -1); // retirar # do fim da string "replica#replica#"
            string[] rep =aux.Split('#');
            List<string> replicas = new List<string>();
            //iniciar lista de replicas
            foreach(var r in rep){
                replicas.Add(r);
            }
            TcpChannel channel = new TcpChannel(Int32.Parse(arguments[0]));
            ChannelServices.RegisterChannel(channel, true);
            
            BrokerCommunication brokerbroker = new BrokerCommunication(lstaux, arguments[2], replicas, arguments[0] );
            RemotingServices.Marshal(brokerbroker, "BrokerCommunication", typeof(BrokerCommunication));

            MPMBrokerCmd processCmd = new MPMBrokerCmd();
            RemotingServices.Marshal(processCmd, "MPMProcessCmd", typeof(MPMBrokerCmd));

            
            Console.ReadLine();
        }
    }

    //IMPLEMENTATIONS

    public class BrokerCommunication : MarshalByRefObject, BrokerReceiveBroker
    {
        // vida infinita !!!!
        public override object InitializeLifetimeService()
        {
            return null;
        }

        public delegate void FilterFloodRemoteAsyncDelegate(Message m, string t, int eventNumber,int order, int logMode);
        public delegate void SubUnsubRemoteAsyncDelegate(string t, string n);

        

        // This is the call that the AsyncCallBack delegate will reference.
        public static void FilterFloodRemoteAsyncCallBack(IAsyncResult ar)
        {
            FilterFloodRemoteAsyncDelegate del = (FilterFloodRemoteAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
            return;
        }

        public static void SubUnsubRemoteAsyncCallBack(IAsyncResult ar)
        {
            SubUnsubRemoteAsyncDelegate del = (SubUnsubRemoteAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
            return;
        }

        private static object myLock = new Object();
        private string name;
        private List<Broker> lstVizinhos;
        private Dictionary<string, List<string>> lstSubsTopic; //quem subscreveu neste no, a quE
        private Dictionary<Broker, List<string>> routingTable; //vizinho,subscrições atingiveis atraves desse vizinho
        private Dictionary<string, int> pubCount; //publisher%topico->numSeq
        private Dictionary<string, int> seqPub; //publisher->numSeq
        private Dictionary<string, int> subPubSeq; //sub%publisher->numSeq
        private List<Message> lstMessage;
        private List<string> lstReplicas;
        private bool isLeader; 
        

        public BrokerCommunication(List<Broker> lst, string n, List<string> replicas, string port)
        {
            name = n;
            lstSubsTopic = new Dictionary<string, List<string>>();
            routingTable = new Dictionary<Broker, List<string>>();
            lstVizinhos = lst;
            pubCount = new Dictionary<string, int>();
            seqPub = new Dictionary<string, int>();
            lstMessage = new List<Message>();
            lstReplicas = replicas;
            isLeader = tryLeader(replicas, port);
            Console.WriteLine("@broker !!! porto -> {0}, isLeader {1}", port, isLeader.ToString());
            //foreach(var a in replicas)
            //{
            //    Console.WriteLine("recplica -> {0}",a);
            //}
            //foreach(var b in lst)
            //{
            //    Console.WriteLine("vizinho -> {0}", b.Name);
            //}
        }

        private bool tryLeader(List<string> replicas, string p)
        {
            List<int> lstPort = new List<int>();
            lstPort.Add(int.Parse(p));
            foreach (var url in replicas)
            {
                string[] a = url.Split(':');
                string b = a[2].Trim('/');
                lstPort.Add(int.Parse(b));
            }

            int leader = lstPort.Min();
            if(!(leader == int.Parse(p)))
            {
                return false;
            }
            return true;
        }

        public List<Message> SortList(List<Message> l)
        {
            int length = l.Count;

            Message temp = l[0];

            for (int i = 0; i < length; i++)
            {
                for (int j = i + 1; j < length; j++)
                {
                    if (l[i].SeqNum > l[j].SeqNum)
                    {
                        temp = l[i];

                        l[i] = l[j];

                        l[j] = temp;
                    }
                }
            }

            return l;
        }

        public void forwardFlood(Message m, string brokerName, int eventNumber, int order, int logMode)
        {
            if (!isLeader)//se não és lider
            {
                //TODO - adicionar à lista de msg , retirar msg se já foi enviada e recebida.
                return;
            }
            string pubTopic = m.author + "%" + m.Topic;
            if (order == 1)//FIFO
            {

                lock (myLock)
                {
                    if (!seqPub.ContainsKey(m.author))
                    {//publisher nao publicou nada
                        seqPub.Add(m.author, 1);//actualizar numSeq deste pub
                    }

                    if (!pubCount.ContainsKey(pubTopic))//publisher nao publicou nada neste topico
                    {
                        pubCount.Add(pubTopic, 1);
                    }

                    if (m.SeqNum == seqPub[m.author])//msg esperada para aquele publisher
                    {
                        foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                        {
                            if (searchTopicList(m.Topic, t.Value))//ha match de topicos
                            {
                                
                                Console.WriteLine("Eu notifiquie o sub interesado, numeroSeq da msg ---> {0}", m.SeqNum);
                                SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                                not.notify(m, eventNumber);
                            }
                        }
                        //ja notifiquei quem tinha a notificar com esta publicacao
                        seqPub[m.author]++;
                        pubCount[pubTopic]++;
                    }
                    else
                    { //nao e a msg esperada

                        Console.WriteLine("{0} - Esperado {1} ----- {2} Recebido", pubTopic, seqPub[m.author], m.SeqNum);
                        lstMessage.Add(m);
                        lstMessage = SortList(lstMessage);
                    }



                    //iterar sobre lista de espera para ver se posso mandar alguma coisa
                    for (int j = 0; j < lstMessage.Count; )
                    {
                        int next = seqPub[m.author];// e o proximo numSeq que estou a espera para aquele autor

                        Console.WriteLine("Estou a iterar a procura do {0} para <{1}>", next, pubTopic);
                        foreach (Message mi in lstMessage) Console.Write("a: " + mi.author + ",n: " + mi.SeqNum + "#");
                        Console.WriteLine("");
                        if (lstMessage[j].author.Equals(m.author) && lstMessage[j].SeqNum == next)
                        {//msg na lista de espera e do mesmo autor, e tambem e a que estava a espera
                            foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                            {
                                if (searchTopicList(lstMessage[j].Topic, t.Value))//ha match de topicos para um sub meu
                                {
                                    Console.WriteLine("Durante a iteracao Eu notifiquie o sub interesado, numeroSeq da msg ---> {0}", lstMessage[j].SeqNum);
                                    SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                                    not.notify(lstMessage[j], eventNumber);
                                }
                            }
                            Console.Write("antes de descartar mensagem : " + lstMessage[j].SeqNum + " #");
                            lstMessage.Remove(lstMessage[j]);

                            j = 0; // voltar ao início da lista
                            seqPub[m.author]++;
                            pubCount[pubTopic]++;
                            Console.WriteLine("seq actual: " + seqPub[m.author]);
                        }
                        else
                        {
                            j++; //continuar a iterar }
                        }
                    }
                }
            }
            else {
                foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                {
                    if (searchTopicList(m.Topic, t.Value))//ha match de topicos
                    {
                        Console.WriteLine("Eu notifiquie o sub interesado, numeroSeq da msg ---> {0}", m.SeqNum);
                        SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                        not.notify(m, eventNumber);
                    }
                }
                //ja notifiquei quem tinha a notificar com esta publicacao
                //seqPub[m.author]++;
                //pubCount[pubTopic]++;
            }


            //PROPAGATION TIME


            List<Broker> lst = new List<Broker>(lstVizinhos);
            //eliminar remetente da lista
            for (int i = 0; i < lst.Count; i++)
            {
                if (lst[i].Name.Equals(brokerName))
                {
                    //Console.WriteLine("Removi o {0} da lista de vizinhos", lst[i].Name);
                    lst.Remove(lst[i]);
                }
            }

            //propagar para os outros todos

            foreach (var viz in lst)
            {
                string urlRemote = viz.URL.Substring(0, viz.URL.Length - 6);//retirar XXXX/broker

                Console.WriteLine("Flooding MSG <<<<{0}>>>>  #seq {1} para o vizinho em {2}", pubTopic, m.SeqNum, urlRemote);
                BrokerReceiveBroker bro = (BrokerReceiveBroker)Activator.GetObject(typeof(BrokerReceiveBroker), urlRemote + "BrokerCommunication");
                try
                {
                    if (logMode == 1)
                    {
                        LogInterface log = (LogInterface)Activator.GetObject(typeof(LogInterface), "tcp://localhost:8086/PuppetMasterLog");
                        log.log(this.name, m.author, m.Topic, eventNumber, "broker");
                    }
                    FilterFloodRemoteAsyncDelegate RemoteDel = new FilterFloodRemoteAsyncDelegate(bro.forwardFlood);
                    AsyncCallback RemoteCallBack = new AsyncCallback(FilterFloodRemoteAsyncCallBack);
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(m, name, eventNumber, order, logMode, RemoteCallBack, null);
                }
                catch (SocketException)
                {
                    Console.WriteLine("Could not locate server");
                }
            }
        }

        

        public void forwardFilter(Message m, string brokerName, int eventNumber, int order, int logMode)
        {
            if (!isLeader)//se não és lider
            {
                //TODO - adicionar à lista de msg , retirar msg se já foi enviada e recebida.
                return;
            }
            Console.WriteLine("ForwardFilter received from broker -> {0}", brokerName);

            string pubTopic = m.author + "%" + m.Topic;

            if (order == 1)//FIFO
            {
                lock (myLock)
                {
                    if (!seqPub.ContainsKey(m.author))
                    {//publisher nao publicou nada
                        seqPub.Add(m.author, 1);
                    }

                    if (!pubCount.ContainsKey(pubTopic))//publisher nao publicou nada neste topico
                    {
                        pubCount.Add(pubTopic, 1);
                    }

                    if (m.SeqNum == seqPub[m.author])//msg esperada para aquele publisher
                    {
                        foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                        {
                            if (searchTopicList(m.Topic, t.Value))//ha match de topicos
                            {
                                Console.WriteLine("Eu notifiquie o sub interesado, numeroSeq da msg ---> {0}", m.SeqNum);
                                SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                                not.notify(m, eventNumber);
                            }
                        }
                        //ja notifiquei quem tinha a notificar com esta publicacao
                        seqPub[m.author]++;
                        pubCount[pubTopic]++;
                    }
                    else
                    { //nao e a msg esperada
                            //Console.WriteLine("{0} - Esperado {1} ----- {2} Recebido", pubTopic, seqPub[m.author], m.SeqNum);
                            lstMessage.Add(m);
                            lstMessage = SortList(lstMessage);
                    }



                    //iterar sobre lista de espera para ver se posso mandar alguma coisa
                    for (int j = 0; j < lstMessage.Count; )
                    {
                        int next = seqPub[m.author];// e o proximo numSeq que estou a espera para aquele autor

                        int seqPubTop = pubCount[pubTopic];//numSeq para aquele topico+publisher

                        //Console.WriteLine("Estou a iterar a procura do {0} para <{1}>", next, pubTopic);
                        foreach (Message mi in lstMessage) Console.Write("a: " + mi.author + ",n: " + mi.SeqNum + "#");
                        //Console.WriteLine("");
                        if (lstMessage[j].author.Equals(m.author) && lstMessage[j].SeqNum == next)//msg na lista de espera e do mesmo autor e a que estou a espera
                        {
                            foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                            {
                                if (searchTopicList(lstMessage[j].Topic, t.Value))//ha match de topicos para um sub meu
                                {
                                    //Console.WriteLine("Durante a iteracao Eu notifiquie o sub interesado, numeroSeq da msg ---> {0}", lstMessage[j].SeqNum);
                                    SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                                    not.notify(lstMessage[j], eventNumber);
                                }
                            }
                            //Console.Write("antes de descartar mensagem : " + lstMessage[j].SeqNum + " #");
                            lstMessage.Remove(lstMessage[j]);

                            j = 0; // voltar ao início da lista
                            seqPub[m.author]++;
                            pubCount[pubTopic]++;
                            //Console.WriteLine("seq actual: " + seqPub[m.author]);
                        }
                        else
                        {
                            j++; //continuar a iterar }
                        }
                        
                    }
                }
            }
            else //modo NO order
            {
                foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
                {

                    if (searchTopicList(m.Topic, t.Value))//ha match de topicos
                    {
                        Console.WriteLine("Eu notifiquie o sub interesado, numeroSeq ---> {0}", m.SeqNum);
                        SubscriberNotify not = (SubscriberNotify)Activator.GetObject(typeof(SubscriberNotify), t.Key + "/Notify");
                        not.notify(m, eventNumber);
                    }
                }
            }


            //PROPAGATION TIME


            Dictionary<Broker, List<string>> lst = new Dictionary<Broker, List<string>>(routingTable);
            //eliminar remetente da routing table
            foreach (KeyValuePair<Broker, List<string>> par in routingTable)
            {
                if (par.Key.Name.Equals(brokerName))
                {
                    lst.Remove(par.Key);
                }
            }

            foreach (KeyValuePair<Broker, List<string>> t in lst)
            {
                string urlRemote = t.Key.URL.Substring(0, t.Key.URL.Length - 6);//retirar XXXX/broker

                //Console.WriteLine("Estou a considerar o {0}", urlRemote);

                BrokerReceiveBroker bro = (BrokerReceiveBroker)Activator.GetObject(typeof(BrokerReceiveBroker), urlRemote + "BrokerCommunication");

                if (searchTopicList(m.Topic, t.Value))
                { //ha alguem na routing table que quer este topico

                    Console.WriteLine("Filtering vizinho em {0} --> #msg em toSend() {1}", urlRemote, m.SeqNum);

                    try
                    {
                        if (logMode == 1)
                        {
                            LogInterface log = (LogInterface)Activator.GetObject(typeof(LogInterface), "tcp://localhost:8086/PuppetMasterLog");
                            log.log(this.name, m.author, m.Topic, eventNumber, "broker");
                        }

                        FilterFloodRemoteAsyncDelegate RemoteDel = new FilterFloodRemoteAsyncDelegate(bro.forwardFilter);
                        AsyncCallback RemoteCallBack = new AsyncCallback(FilterFloodRemoteAsyncCallBack);
                        IAsyncResult RemAr = RemoteDel.BeginInvoke(m, name, eventNumber, order, logMode, RemoteCallBack, null);

                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Could not locate server");
                    }
                }
                else
                {
                    Message dummy = new Message("null", "null", m.SeqNum, m.author);

                    //Console.WriteLine("----------Messagem dummy---------------");

                    try
                    {
                        /*if (logMode == 1)
                        {
                            LogInterface log = (LogInterface)Activator.GetObject(typeof(LogInterface), "tcp://localhost:8086/PuppetMasterLog");
                            log.log(this.name, m.author, m.Topic, eventNumber, "broker");
                        }*/

                        FilterFloodRemoteAsyncDelegate RemoteDel = new FilterFloodRemoteAsyncDelegate(bro.forwardFilter);
                        AsyncCallback RemoteCallBack = new AsyncCallback(FilterFloodRemoteAsyncCallBack);
                        IAsyncResult RemAr = RemoteDel.BeginInvoke(dummy, name, eventNumber, order, logMode, RemoteCallBack, null);

                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Could not locate server");
                    }

                }
            }
        }

        
        public void forwardSub(string topic, string brokerName)
        {

            //buscar broker com nome brokerName
            foreach (var v in lstVizinhos)
            {

                if (v.Name.Equals(brokerName))
                {
                    Broker aux = v;
                    if (routingTable.ContainsKey(aux))//ja tenho uma entrada para este broker
                    {
                        if (!routingTable[aux].Contains(topic))//adicionar apenas se for outro topico
                        {
                            routingTable[aux].Add(topic);

                        }
                    }
                    else
                    {
                        routingTable[aux] = new List<string> { topic };
                    }


                }
            }

            List<Broker> lst = new List<Broker>(lstVizinhos);
            //eliminar remetente da lista
            for (int i = 0; i < lst.Count; i++)
            {
                if (lst[i].Name.Equals(brokerName))
                {
                    //Console.WriteLine("Removi o {0} da lista de vizinhos", lst[i].Name);
                    lst.Remove(lst[i]);
                }
            }

            //propagar para os outros todos
            if (!isLeader)//se não és lider não propagas
            {
                return;
            }
            foreach (var viz in lst)
            {
                string urlRemote = viz.URL.Substring(0, viz.URL.Length - 6);//retirar XXXX/broker
                

                Console.WriteLine("Flooding vizinho em {0}", urlRemote);
                BrokerReceiveBroker bro = (BrokerReceiveBroker)Activator.GetObject(typeof(BrokerReceiveBroker), urlRemote + "BrokerCommunication");
                try
                {
                    SubUnsubRemoteAsyncDelegate RemoteDel = new SubUnsubRemoteAsyncDelegate(bro.forwardSub);
                    AsyncCallback RemoteCallBack = new AsyncCallback(SubUnsubRemoteAsyncCallBack);
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(topic, name, RemoteCallBack, null);
                  
                }
                catch (SocketException)
                {
                    Console.WriteLine("Could not locate server");
                }
            }

        }
        public void forwardUnsub(string topic, string brokerName)
        {
            //Console.WriteLine("unsub on topic {0} received from {1}", topic, brokerName);

            //buscar broker com nome brokerName
            foreach (var v in lstVizinhos)
            {
                if (v.Name.Equals(brokerName))
                {
                    Broker aux = v;
                    if (routingTable.ContainsKey(aux))//ja tenho uma entrada para este broker
                    {
                        routingTable[aux].Remove(topic);
                    }
                }
            }

            List<Broker> lst = new List<Broker>(lstVizinhos);
            //eliminar remetente da lista
            for (int i = 0; i < lst.Count; i++)
            {
                if (lst[i].Name.Equals(brokerName))
                {
                    //Console.WriteLine("Removi o {0} da lista de vizinhos", lst[i].Name);
                    lst.Remove(lst[i]);
                }
            }

            //propagar para os outros todos
            if (!isLeader)//se não és lider não propagas
            {
                return;
            }
            foreach (var viz in lst)
            {
                string urlRemote = viz.URL.Substring(0, viz.URL.Length - 6);//retirar XXXX/broker
                

                //Console.WriteLine("Flooding vizinho em {0}", urlRemote);
                BrokerReceiveBroker bro = (BrokerReceiveBroker)Activator.GetObject(typeof(BrokerReceiveBroker), urlRemote + "BrokerCommunication");
                try
                {
                    SubUnsubRemoteAsyncDelegate RemoteDel = new SubUnsubRemoteAsyncDelegate(bro.forwardUnsub);
                    AsyncCallback RemoteCallBack = new AsyncCallback(SubUnsubRemoteAsyncCallBack);
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(topic, name, RemoteCallBack, null);

                }
                catch (SocketException)
                {
                    Console.WriteLine("Could not locate server");
                }

            }
        }
        public void receiveSub(string topic, string subName)
        {
            Console.WriteLine("sub on topic {0} received from subscriber -> {1}", topic, subName);
            if (lstSubsTopic.ContainsKey(subName))
            {
                //Console.WriteLine("Ja tinha uma subs para este tipo");
                lstSubsTopic[subName].Add(topic);
            }
            else
            {
                //Console.WriteLine("Nao tinha nenhuma sub para este tipo");
                lstSubsTopic[subName] = new List<string> { topic };
            }
            if (isLeader)
            {
                forwardSub(topic, name);
            }
            else
            {
                return;
            }
        }
        public void receiveUnsub(string topic, string subName)
        {
            Console.WriteLine("unsub on topic {0} received from {1}", topic, subName);
            if (lstSubsTopic.ContainsKey(subName))
            {
                lstSubsTopic[subName].Remove(topic);
            }
            if (isLeader)
            {
                forwardUnsub(topic, name);
            }
            else
            {
                return;
            }
        }

        public void receivePublication(Message m, string pubName, int filter,int order, int eventNumber, int logMode)
        {   
            Console.WriteLine("Recebi publicacao no eventNumber {0} e NumSeq {1}",eventNumber,m.SeqNum);
            if (isLeader)
            {
                if (filter == 0)
                {
                    forwardFlood(m, name, eventNumber, order, logMode);
                }
                else
                {
                    forwardFilter(m, name, eventNumber, order, logMode);
                }
            }
            else
            {
                return;
            }
                
        }
        public void status()
        {

            Console.WriteLine("I'm Broker {0}\r\n", name);
            Console.WriteLine("These are my Broker neighbours:");

            foreach (Broker b in lstVizinhos)
            {
                Console.WriteLine("\tBroker {0}\r\n", b.Name);
            }
            Console.WriteLine("these are my subTopics:");
            foreach (KeyValuePair<string, List<string>> t in lstSubsTopic)
            {
                foreach(string s in t.Value)
                {
                    Console.WriteLine("Sub:{0} -> topic: {1}\r\n", t.Key, s);
                }
               
            }
        }

        public bool searchTopicList(string topic, List<string> subs)
        {
            foreach (string s in subs)
            {
                if (isInterested(topic, s))
                {
                    return true;
                }
            }

            return false;
        }

        public bool isInterested(string topic, string sub)
        {
            if (topic.Equals(sub)) { return true; }

            
            string[] topicV = topic.Split('/');
            string[] subV = sub.Split('/');

            if (topicV.Length < subV.Length)
            {
                return false;
            }

            for (int i = 0; i < subV.Length; i++)
            {
                if (i == subV.Length - 1)
                {
                    if (subV[i].Equals("*"))
                    { 
                        return true;
                    }
                    else
                    {
                        
                        return false;
                    }
                }
                if (!topicV[i].Equals(subV[i]))
                {
                    
                    return false;
                }
                
            }
            
            return false;

        }

    }

    public class MPMBrokerCmd : MarshalByRefObject, IProcessCmd
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
