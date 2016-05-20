using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.Storage;
using System.Configuration;
using ClassLibrary;
using Microsoft.WindowsAzure.Storage.Table;
using System.IO;
using Microsoft.WindowsAzure.Storage.Queue;
using HtmlAgilityPack;
using System.Xml;
using ClassLibrary1;
using System.Linq;

namespace WorkerRole1
{
    public class Worker : RoleEntryPoint
    {
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent runCompleteEvent = new ManualResetEvent(false);
        private PerformanceCounter cpuCounter;
        private PerformanceCounter memCounter;
        private CloudStorageAccount storageAccount;
        private List<string> disallowed;
        private HashSet<string> alreadyCrawled;
        private string workerState;
        //Keeps track of Table Index
        private int indexCount;
        //Keeps Track of Add Count to maintain proper Table Index after restarting
        private int currentCount;
        //Last 10 urls crawled
        private List<string> lastTen;
        //Worker Stats for the last hour
        private List<string> hourPerf;
        //Keeps track of the minutes
        private int minuteCount;

        public override void Run()
        {
            Trace.TraceInformation("WorkerRole1 is running");

            // Instantiating storageAccount
            storageAccount = CloudStorageAccount.Parse(
                   ConfigurationManager.AppSettings["StorageConnectionString"]);

            CloudQueue siteMapQueue = GetQueue("sitemapqueue");
            CloudQueue urlQueue = GetQueue("urlqueue");
            CloudQueue workerStateQueue = GetQueue("workeractivate");
            siteMapQueue.Clear();
            urlQueue.Clear();
            workerStateQueue.Clear();

            //Instantiating disallowed list & alreadyCrawled HashSet
            disallowed = new List<string>();
            alreadyCrawled = new HashSet<string>();

            // Initial Sitemaps
            //EnqueueMessage("sitemapqueue", "http://www.cnn.com");
            //EnqueueMessage("sitemapqueue", "http://www.bleacherreport.com");
            EnqueueMessage("urlqueue", "go.cnn.com/?stream=cnni&sr=watchLiveHPbutton&source=edition");

            // Initializing Worker State
            EnqueueMessage("workeractivate", "true");

            // Setting up performance tracking over time
            Timer performanceTimer = new Timer(updatePerformance, null, 3000, 5000);
            cpuCounter = new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName);
            memCounter = new PerformanceCounter("Process", "Working Set", Process.GetCurrentProcess().ProcessName);
            hourPerf = new List<string>();

            lastTen = new List<string>();
            // Setting up last 10 urls crawled list
            for (int i = 0; i < 10; i++)
            {
                lastTen.Add("No Data");
            }

            while (true)
            {
                // Check Worker State
                CloudQueueMessage msg = workerStateQueue.GetMessage();
                if (msg != null)
                {
                    workerState = msg.AsString;
                    workerStateQueue.DeleteMessage(msg);
                }

                //Active State
                if (workerState == "true")
                {
                    // Get all robots.txt info for each website 
                    msg = siteMapQueue.GetMessage();
                    while (msg != null && msg.AsString.EndsWith(".com"))
                    {
                        getRobots(msg.AsString);
                        siteMapQueue.DeleteMessage(msg);
                        msg = siteMapQueue.GetMessage();
                    }

                    // Parse all sitemap XMLs and add to HTML URL queue
                    if (msg != null)
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.Load(msg.AsString);
                        using (XmlTextReader tr = new XmlTextReader(msg.AsString))
                        {
                            tr.Namespaces = false;
                            doc.Load(tr);
                        } 

                        var sitemaps = doc.SelectNodes("//sitemap");

                        if ( doc.SelectNodes("//sitemap").Count == 0)
                        {
                            sitemaps = doc.SelectNodes("//url");
                        }
                        foreach (XmlElement sitemap in sitemaps)
                        {
                            if ( workerState != "true")
                            {
                                break;
                            }
                            if (!alreadyCrawled.Contains(sitemap.SelectSingleNode("loc").InnerText))
                            {
                                if (sitemap.SelectSingleNode("lastmod") != null)
                                {

                                    if (DateTime.Parse(sitemap.SelectSingleNode("lastmod").InnerText) >= DateTime.Parse("03/01/2016"))
                                    {
                                        // Adds to sitemap queue if xml file type
                                        if (sitemap.SelectSingleNode("loc").InnerText.Contains(".xml") && (sitemap.SelectSingleNode("loc").InnerText.Contains(".cnn.com")))
                                        {
                                            alreadyCrawled.Add(sitemap.SelectSingleNode("loc").InnerText);
                                            EnqueueMessage("sitemapqueue", sitemap.SelectSingleNode("loc").InnerText);
                                        }
                                        // Adds to regular url queue if standard url
                                        else if (sitemap.SelectSingleNode("loc").InnerText.Contains(".cnn.com"))
                                        {
                                            alreadyCrawled.Add(sitemap.SelectSingleNode("loc").InnerText);
                                            EnqueueMessage("urlqueue", sitemap.SelectSingleNode("loc").InnerText);
                                        }
                                    }
                                }
                                else if ((sitemap.SelectSingleNode("lastmod") == null) && (sitemap.SelectSingleNode("loc").InnerText.Contains(".cnn.com") || sitemap.SelectSingleNode("loc").InnerText.Contains("bleacherreport.com")))
                                {
                                    bool isAllowed = true;
                                    foreach (string disallowedURL in disallowed)
                                    {
                                        if (sitemap.SelectSingleNode("loc").InnerText.Contains(disallowedURL) == true)
                                        {
                                            isAllowed = false;
                                            break;
                                        }
                                    }
                                    if (isAllowed)
                                    {
                                        alreadyCrawled.Add(sitemap.SelectSingleNode("loc").InnerText);
                                        EnqueueMessage("urlqueue", sitemap.SelectSingleNode("loc").InnerText);
                                    }
                                }
                            }
                        }
                            siteMapQueue.DeleteMessage(msg);
                    }

                    if (msg == null)
                    {
                        // Parse one url from the url queue
                        CloudQueueMessage urlMsg = urlQueue.GetMessage();
                        if (urlMsg != null)
                        {
                            HtmlWeb web = new HtmlWeb();
                            try
                            {
                                HtmlDocument document = web.Load(urlMsg.AsString);
                                var allHrefNodes = document.DocumentNode.SelectNodes(".//a[@href]");
                                if (allHrefNodes != null)
                                {
                                    foreach (HtmlNode item in allHrefNodes)
                                    {
                                        string currentHref = item.GetAttributeValue("href", string.Empty);
                                        if (currentHref.StartsWith("/") && currentHref != "/")
                                        {
                                            if (currentHref.StartsWith("//"))
                                            {
                                                currentHref = currentHref.Replace("//", "http://");
                                            }
                                            else
                                            {
                                                // Creates full url out of relative url
                                                var url = urlMsg.AsString.Split(new string[] { ".com" }, StringSplitOptions.None);
                                                currentHref = url[0] + ".com" + currentHref;
                                            }
                                        }
                                        if ((currentHref.Contains(".cnn.com") || currentHref.Contains("bleacherreport.com/articles")) && currentHref != "/")
                                        {
                                            // Seperating the already crawled check in order to only check when the domain is correct 
                                            if (!alreadyCrawled.Contains(currentHref))
                                            {
                                                bool isAllowed = true;
                                                foreach (string disallowedURL in disallowed)
                                                {
                                                    if (currentHref.Contains(disallowedURL) == true)
                                                    {
                                                        isAllowed = false;
                                                        break;
                                                    }
                                                }
                                                if (isAllowed)
                                                {
                                                    EnqueueMessage("urlqueue", currentHref);
                                                }
                                                alreadyCrawled.Add(currentHref);
                                            }
                                        }
                                    }
                                }

                                if (document.DocumentNode.SelectSingleNode("//title") != null)
                                {
                                    if (document.DocumentNode.SelectSingleNode("//meta[@itemprop='datePublished']") != null)
                                    {
                                        AddToTable(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml, document.DocumentNode.SelectSingleNode("//meta[@itemprop='datePublished']").Attributes["content"].Value);
                                    }
                                    else if (document.DocumentNode.SelectSingleNode("//meta[@name='pubdate']") != null)
                                    {
                                        AddToTable(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml, document.DocumentNode.SelectSingleNode("//meta[@name='pubdate']").Attributes["content"].Value);
                                    }
                                    else if ((document.DocumentNode.SelectSingleNode("//meta[@name='description']") != null))
                                    {
                                        if (!document.DocumentNode.SelectSingleNode("//meta[@name='description']").Attributes["content"].Value.Contains("Error"))
                                        {
                                            AddToTable(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml, "Undated");
                                        }
                                        else
                                        {
                                            //CNN error pages
                                            AddToErrors(urlMsg.AsString, "Page Not Found");
                                        }
                                    }
                                    else if (document.DocumentNode.SelectSingleNode("//meta[@name='Description']") != null)
                                    {
                                        if (!document.DocumentNode.SelectSingleNode("//meta[@name='Description']").Attributes["content"].Value.Contains("Error"))
                                        {
                                            AddToTable(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml, "Undated");
                                        }
                                        else
                                        {
                                            //CNN error pages
                                            AddToErrors(urlMsg.AsString, "Page Not Found");
                                        }
                                    }
                                    //Bleacher Report error pages
                                    else if (document.DocumentNode.SelectSingleNode("//title").InnerHtml == "Bleacher Report")
                                    {
                                        AddToErrors(urlMsg.AsString, "Page Not Found");
                                    }
                                    // For old CNN pages
                                    else if (document.DocumentNode.SelectSingleNode("//title").InnerHtml.ToLower().Contains("allpolitics") ||
                                             document.DocumentNode.SelectSingleNode("//title").InnerHtml.ToLower().Contains("cnn"))
                                    {
                                        AddToTable(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml, "Undated");
                                    }
                                    else
                                    {
                                        AddToErrors(urlMsg.AsString, document.DocumentNode.SelectSingleNode("//title").InnerHtml);
                                    }
                                }
                            }
                            catch (System.Net.WebException exc)
                            {
                                Debug.WriteLine("Connection rejected for HTMLAgilityPack");
                            }
                            catch (System.UriFormatException ex)
                            {
                                Debug.WriteLine("Improperly Formed URL");
                            }
                            alreadyCrawled.Add(urlMsg.AsString);
                            urlQueue.DeleteMessage(urlMsg);
                        }
                    }
                }
                // Idle worker for 40 seconds in order to delete table and prevent table issues
                else if (workerState == "wait")
                {
                    // Clear Queues
                    CloudQueue queue = GetQueue("sitemapqueue");
                    queue.Clear();
                    queue = GetQueue("urlqueue");
                    queue.Clear();

                    // Stop Timer while clearing table
                    performanceTimer.Change(Timeout.Infinite, Timeout.Infinite);

                    // Ensure table is updated completely before clearing
                    Thread.Sleep(500);

                    // Clear Tables
                    CloudTable table = GetTable("sitesData");
                    table.DeleteIfExists();
                    table = GetTable("errorData");
                    table.DeleteIfExists();

                    disallowed.Clear();
                    Debug.WriteLine("Waiting Worker State");
                    Thread.Sleep(40000);

                    // Restart timer
                    performanceTimer.Change(3000, 5000);
                    indexCount = 0;
                    currentCount = 0;

                    workerState = "false";
                }
                // Idle State
                else
                {
                    disallowed.Clear();
                    Debug.WriteLine("Idle Worker State");
                    Thread.Sleep(50);
                    currentCount = 0;
                }

            }
        }

        public override bool OnStart()
        {

            // Set the maximum number of concurrent connections
            ServicePointManager.DefaultConnectionLimit = 12;

            // For information on handling configuration changes
            // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

            bool result = base.OnStart();

            Trace.TraceInformation("WorkerRole1 has been started");
            RoleEnvironment.TraceSource.Switch.Level = SourceLevels.Information;
        
            return result;
        }

        public override void OnStop()
        {
            Trace.TraceInformation("WorkerRole1 is stopping");

            this.cancellationTokenSource.Cancel();
            this.runCompleteEvent.WaitOne();

            base.OnStop();

            Trace.TraceInformation("WorkerRole1 has stopped");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following with your own logic.
            while (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceInformation("Working");
                await Task.Delay(1000);
            }
        }

        //Add Crawled Data to Table
        private void AddToTable(string url, string title, string time) {
            CloudTable table = GetTable("sitesData");
            table.CreateIfNotExists();

            //Add/Update Counter
            currentCount++;
            if (currentCount >= indexCount)
            {
                indexCount++;
            }
            Counter userCount = new Counter(indexCount);
            TableOperation insert = TableOperation.InsertOrReplace(userCount);
            table.Execute(insert);

            Website result = new Website(url, title, time);
            insert = TableOperation.InsertOrReplace(result);
            table.Execute(insert);

            //Add to the list
            lastTen.RemoveAt(0);
            lastTen.Add(url);

            string list = "";

            foreach (string link in lastTen)
            {
                list += link + ",";
            }
            Website tenSites = new Website(list.Substring(0, list.Length - 1));
            insert = TableOperation.InsertOrReplace(tenSites);
            table.Execute(insert);

        }

        //Add errors to error table
        private void AddToErrors(string url, string message)
        {
            CloudTable table = GetTable("errorData");
            table.CreateIfNotExists();

            Website result = new Website(url, message);
            TableOperation insertOperation = TableOperation.InsertOrReplace(result);
            table.Execute(insertOperation);
        }

        //Updates performance information and status
        private void updatePerformance(object state)
        {
            CloudTable table = GetTable("sitesData");
            double cpu = cpuCounter.NextValue();
            double ram = memCounter.NextValue();
            table.CreateIfNotExists();
            Stats result = new Stats(cpu, (ram / 1024 / 1024));
            TableOperation insertOperation = TableOperation.InsertOrReplace(result);
            table.Execute(insertOperation);

            //To track worker performance over the last hour
            //Adds one performance update every 30 seconds
            minuteCount++;
            if (minuteCount >= 6)
            {
                //Remove the oldest logged performance stat if more than an hour of stats is logged
                if (hourPerf.Count > 120)
                {
                    hourPerf.RemoveAt(0);
                }
                hourPerf.Add(cpu.ToString() + " " + (ram / 1024 / 1024).ToString());
                string loggedStats = "";
                foreach ( string loggedStat in hourPerf)
                {
                    loggedStats += loggedStat + "|";
                }
                Stats perfList = new Stats(loggedStats);
                insertOperation = TableOperation.InsertOrReplace(perfList);
                table.Execute(insertOperation);
                minuteCount = 0;
            }

            Status currentStatus = new Status(workerState);
            insertOperation = TableOperation.InsertOrReplace(currentStatus);
            table.Execute(insertOperation);
        }

        //Checks Robots.txt 
        private void getRobots(string url)
        {
            WebClient wClient = new WebClient();
            Stream data = wClient.OpenRead(url + "/robots.txt");
            StreamReader read = new StreamReader(data);

            string nextLine = read.ReadLine();
            string user = "";
            while (nextLine != null)
            {
                string[] lines = nextLine.Split(null);
                if (lines[0].Contains("User-Agent:"))
                {
                    user = lines[1];
                } 
                else if (lines[0].Contains("Sitemap:"))
                {
                    if (lines[1].Contains("cnn.com") || lines[1].Contains("bleacherreport.com/sitemap/nba"))
                    {
                        EnqueueMessage("sitemapqueue", lines[1]);
                    }
                } else if ((lines[0].Contains("Allow:") && (lines[0].Contains(".html") || lines[0].Contains(".htm"))) && user == "*")
                {
                    EnqueueMessage("urlqueues", url + lines[1]);
                } else if ( user == "*" )
                {
                    disallowed.Add(url.Replace("http://www.", "") + lines[1]);
                }
                nextLine = read.ReadLine();
            }
        }
        
        // Create a table
        private CloudTable GetTable(string tableName)
        {
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();
            CloudTable table = tableClient.GetTableReference(tableName);
            return table;
        }

        // Create a queue
        private CloudQueue GetQueue(string queueName)
        {
            CloudQueueClient queueClient = storageAccount.CreateCloudQueueClient();
            CloudQueue queue = queueClient.GetQueueReference(queueName);
            queue.CreateIfNotExists();
            return queue;
        }

        // Enqueues a message
        private void EnqueueMessage(string queueName, string qMessage)
        {
            alreadyCrawled.Add(qMessage);
            CloudQueue queue = GetQueue(queueName);
            CloudQueueMessage message = new CloudQueueMessage(qMessage);
            queue.AddMessage(message);
        }

    }
}
