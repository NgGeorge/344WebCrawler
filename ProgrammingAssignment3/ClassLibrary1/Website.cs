using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Security.Cryptography;
using System.Text;


namespace ClassLibrary
{
    public class Website : TableEntity
    {
        //For getting a hash or the last 10 websites
        public Website(string url)
        {
            this.PartitionKey = "websiteList";
            this.RowKey = "siteList";
            this.url = url;
        }
        
        //Standard website class
        public Website(string url, string title, string date)
        {
            this.PartitionKey = "website";
            this.RowKey = CalculateMD5Hash(url);

            this.url = url;
            this.title = title;
            this.date = date;
        }

        //Error Sites
        public Website(string url, string error) {
            this.PartitionKey = "errorSite";
            this.RowKey = string.Format("{0:D19}", DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks);

            this.url = url;
            this.error = error;
        }

        public Website() { }

        public string url { get; set; }
        public string title { get; set; }
        public string date { get; set; }
        public string error { get; set; }

        public string CalculateMD5Hash(string input)

        {

            // step 1, calculate MD5 hash from input

            MD5 md5 = MD5.Create();

            byte[] inputBytes = Encoding.ASCII.GetBytes(input);

            byte[] hash = md5.ComputeHash(inputBytes);

            // step 2, convert byte array to hex string

            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < hash.Length; i++)

            {

                sb.Append(hash[i].ToString("X2"));

            }

            return sb.ToString();

        }
    }
}
