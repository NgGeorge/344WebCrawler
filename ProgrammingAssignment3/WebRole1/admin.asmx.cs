using ClassLibrary;
using ClassLibrary1;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Table;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Script.Serialization;
using System.Web.Script.Services;
using System.Web.Services;

namespace WebRole1
{
    /// <summary>
    /// Summary description for WebService1
    /// </summary>
    [WebService(Namespace = "http://tempuri.org/")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    [ScriptService]
    [System.ComponentModel.ToolboxItem(false)]
    // To allow this Web Service to be called from script, using ASP.NET AJAX, uncomment the following line. 
    // [System.Web.Script.Services.ScriptService]
    public class WebService1 : System.Web.Services.WebService
    {
        private CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]);
        private int urlsCrawled = 0;

        // Tells the worker to start crawling
        [WebMethod]
        public void StartCrawling()
        {
            // Adding websites to Queue
            EnqueueMessage("sitemapqueue", "http://www.cnn.com");
            EnqueueMessage("sitemapqueue", "http://www.bleacherreport.com");

            // Start worker message 
            EnqueueMessage("workeractivate", "true");
        }

        // Tells the worker to stop crawling
        [WebMethod]
        public void StopCrawling()
        {
            // Stop worker message 
            EnqueueMessage("workeractivate", "false");
        }

        // Clears the site queue as well as the table of data 
        [WebMethod]
        public void ClearIndex()
        {
            // Adds current number of URLs in table to total number crawled
            urlsCrawled += TableIndexCount();

            // Sends worker a message to wait as the table is being deleted before reactivation
            EnqueueMessage("workeractivate", "wait");

        }

        // Checks if the current url exists in the websites table and returns the title
        [WebMethod]
        [ScriptMethod(ResponseFormat = ResponseFormat.Json)]
        public string GetPageTitle(string url)
        {
            CloudTable table = GetTable("sitesData");
            List<string> title = new List<string>();
            // Only allow query if table exists, otherwise return no data to avoid concurrency issues incase index was cleared recently.
            if (table.Exists())
            {
                Website currentPage = new Website(url);
                var query = new TableQuery<Website>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "website"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, currentPage.CalculateMD5Hash(url))
                    ));

                var result = table.ExecuteQuery(query);

                foreach(Website entity in result)
                {
                    title.Add(entity.title.Replace(",", ""));
                    title.Add(entity.date);
                    title.Add(entity.url);
                    return new JavaScriptSerializer().Serialize(title);
                }
            }
            title.Add("No Data Exists");
            return new JavaScriptSerializer().Serialize(title);
        }

        // Gets all Stats
        [WebMethod]
        [ScriptMethod(ResponseFormat = ResponseFormat.Json)]
        public string GetAllStats()
        {
            List<string> stats = new List<string>();
            // Worker Status
            stats.Add(GetWorkerStatus());
            // Crawler Performance
            foreach (string stat in CrawlerStats())
            {
                stats.Add(stat);
            }
            // Total URLs crawled
            stats.Add(GetTotalUrlsCrawled().ToString());
            // Last Ten URLs crawled
            foreach (string site in GetLastTen())
            {
                stats.Add(site);
            }
            // Urls still in Queue
            stats.Add(URLsLeft().ToString());
            // Table Index
            stats.Add(TableIndexCount().ToString());
            // Any Errors
            foreach (string error in GetErrors())
            {
                stats.Add(error);
            }
            return new JavaScriptSerializer().Serialize(stats);
        }

        //Gets worker status
        private string GetWorkerStatus()
        {
            CloudTable table = GetTable("sitesData");
            if (table.Exists())
            {
                TableQuery<Status> rangeQuery = new TableQuery<Status>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "workerStatusPK"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, "workerStatusRK")
                    ));

                foreach (Status entity in table.ExecuteQuery(rangeQuery))
                {
                    if (entity.status.Contains("true"))
                    {
                        return "Active";
                    } else if (entity.status.Contains("false"))
                    {
                        return "Idle";
                    } else
                    {
                        return "Clearing";
                    }
                }
            }
            return "Clearing";
        }

        // Gets all the Errors
        private List<string> GetErrors()
        {
            CloudTable table = GetTable("errorData");
            List<string> errorList = new List<string>();
            // Only allow query if table exists, otherwise return no data to avoid concurrency issues incase index was cleared recently.
            if (table.Exists())
            {
                var query = new TableQuery<Website>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "errorSite"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.GreaterThan, "0")
                    ));

                var result = table.ExecuteQuery(query);

                foreach (Website entity in result)
                {
                    errorList.Add(entity.url + "|||" + entity.error.Replace(",", ""));
                }

                return errorList;
            } else
            {
                errorList.Add("No ||| Data");
                return errorList;
            }
        }

        // Gets last 10 urls
        private List<string> GetLastTen()
        {
            CloudTable table = GetTable("sitesData");
            List<string> lastTen = new List<string>();
            // Only allow query if table exists, otherwise return no data to avoid concurrency issues incase index was cleared recently.
            if (table.Exists())
            {
                Website currentPage = new Website();
                var query = new TableQuery<Website>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "websiteList"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, "siteList")
                    ));
                var result = table.ExecuteQuery(query);
                foreach (Website entity in result)
                {
                    var links = entity.url.Split(',');
                    foreach (string link in links)
                    {
                        lastTen.Add(link);
                    }
                }
            }
            // In order to maintain formatting for a getting stats
            for (int i = lastTen.Count; i < 10; i++)
            {
                lastTen.Add("No Data");
            }
            return lastTen;
        }

        // Returns the number of URLs currently crawled
        private int TableIndexCount()
        {
            CloudTable table = GetTable("sitesData");
            if (table.Exists())
            {
                TableQuery<Counter> rangeQuery = new TableQuery<Counter>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "counter"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, "CounterRK")
                    ));

                foreach (Counter entity in table.ExecuteQuery(rangeQuery))
                {
                    if (entity.count > 0)
                    {
                        return entity.count;
                    } else
                    {
                        return 0;
                    }
                }
            }
            return 0;
        }

        private int GetTotalUrlsCrawled()
        {
            return urlsCrawled + TableIndexCount();
        }

        // Returns the current performance stats 
        private List<string> CrawlerStats()
        {
            List<string> stats = new List<string>();
            CloudTable table = GetTable("sitesData");
            if (table.Exists())
            {
                TableQuery<Stats> rangeQuery = new TableQuery<Stats>().Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "statCounter"),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, "stat")
                    ));
                foreach (Stats entity in table.ExecuteQuery(rangeQuery))
                {
                    stats.Add(entity.cpu.ToString());
                    stats.Add(entity.memory.ToString());
                }
                return stats;
            } else
            {
                stats.Add("No Data");
                stats.Add("No Data");
                return stats;
            }
        }

        // Returns the number of URLs still left in the pipeline to be crawled
        private int URLsLeft()
        {
            CloudQueue queue = GetQueue("urlqueue");
            queue.FetchAttributes();
            if (queue.ApproximateMessageCount.HasValue)
            {
                return queue.ApproximateMessageCount.Value;
            }
            return 0;
        }

        // Enqueues a message
        private void EnqueueMessage(string queueName, string qMessage)
        {
            CloudQueue queue = GetQueue(queueName);
            CloudQueueMessage message = new CloudQueueMessage(qMessage);
            queue.AddMessage(message);
        }

        // Create a table
        private CloudTable GetTable(string tableRef)
        {
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();
            CloudTable table = tableClient.GetTableReference(tableRef);
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
    }
}
