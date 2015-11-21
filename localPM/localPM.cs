using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Text;
using System.Threading.Tasks;

namespace PubSub
{
    class localPM
    {


        static void Main(string[] args)
        {
            Console.WriteLine("@localPM !!! porto -> {0}", args[0]);

            TcpChannel channel = new TcpChannel(Int32.Parse(args[0]));
            ChannelServices.RegisterChannel(channel, true);

            PMcreateProcess createProcess = new PMcreateProcess(Int32.Parse(args[0]));
            RemotingServices.Marshal(createProcess, "PuppetMasterURL", typeof(PMcreateProcess));

            Console.ReadLine();
        }
    }

    class PMcreateProcess : MarshalByRefObject, PuppetInterface
    {
        int portCounter;
        private static string proj_path = Directory.GetParent(Directory.GetParent(Directory.GetCurrentDirectory()).Parent.FullName).FullName;

        public PMcreateProcess(int pC)
        {
            portCounter = pC+1;
        }


        public void createProcess(TreeNode site, string role, string name, string s, string url,List<string> urlBroker)
        {

            string aux = "LocalPMcreateProcess @ url -> " + url + " site -> " + s;
            Console.WriteLine(aux);

            
            if (role.Equals("broker"))
            {

                string[] z = url.Split(':');//z[0]->tcp;z[1]->//localhost;z[2]->XXXX/broker
                string[] y = z[2].Split('/');
                string port = y[0];


                string brokers = fillArgument(site);

                ProcessStartInfo startInfo = new ProcessStartInfo(proj_path + @"\Broker\bin\Debug\Broker.exe");
                string[] args = { port, url, name, s, brokers };
                    
                startInfo.Arguments = String.Join(";", args);

                Process p = new Process();
                p.StartInfo = startInfo;

                p.Start();


            }
            if (role.Equals("subscriber"))
            {
                string[] z = url.Split(':');//z[0]->tcp;z[1]->//localhost;z[2]->XXXX/broker
                string[] y = z[2].Split('/');
                string port = y[0];

                ProcessStartInfo startInfo = new ProcessStartInfo(proj_path + @"\Subscriber\bin\Debug\Subscriber.exe");
              
                string[] args = { port, url, name, s };

                startInfo.Arguments = String.Join(";", args)+ ";" +String.Join(";",urlBroker);

                Process p = new Process();
                p.StartInfo = startInfo;

                p.Start();
            }
            if (role.Equals("publisher"))
            {
                string[] z = url.Split(':');//z[0]->tcp;z[1]->//localhost;z[2]->XXXX/broker
                string[] y = z[2].Split('/');
                string port = y[0];

                ProcessStartInfo startInfo = new ProcessStartInfo(proj_path + @"\Publisher\bin\Debug\Publisher.exe");
                string[] args = { port, url, name, s};
                startInfo.Arguments = String.Join(";", args) + ";" + String.Join(";", urlBroker);

                Process pro = new Process();
                pro.StartInfo = startInfo;

                pro.Start();
            }

        }

        private string fillArgument(TreeNode site)
        {
            string res = "";
            foreach (var aux in site.getVizinhos()) {
                res += aux.Key + "%" + aux.Value+";";
            }
            return res;
        }
    }
}
