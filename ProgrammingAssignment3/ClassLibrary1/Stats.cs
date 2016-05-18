using Microsoft.WindowsAzure.Storage.Table;

namespace ClassLibrary
{
    public class Stats : TableEntity
    {
        public Stats(double cpu, double memory)
        {
            this.PartitionKey = "statCounter";
            this.RowKey = "stat";

            this.memory = memory;
            this.cpu = cpu;
        }

        public Stats() { }

        public double memory { get; set; }

        public double cpu { get; set; }

    }
}
