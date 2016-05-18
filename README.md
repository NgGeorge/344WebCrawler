# 344WebCrawler
George Ng
INFO 344

Azure URL : http://webcrawler344.cloudapp.net/dashboard.html
Github URL : https://github.com/NgGeorge/344WebCrawler

Implementation : 
I began the project by creating two separate roles in a cloud service, the admin web role and the worker worker role. 

The core functionality of the web crawler was written in the worker role. It functions by reading in a message telling it to either "start", "stop", or "clear indexes" from a workerstate queue. It also initializes a timer on starting that manages and updates the worker's status and performance to an azure table. The worker begins by taking in a few "root sites" such as cnn.com and bleacherreport, and then passing those sites into a function that parses the robots.txt for all root sites. After the robots.txt is parsed, the worker than parses the sitemaps, one sitemap per loop cycle of the worker. Any regular htm/l webpages are added to a url queue to be crawled, while any xml pages are added to the sitemap pages to be processed. For the cnn.com domain, sitemaps and pages were only allowed to be added if they were last modified or published no earlier than March 1st, 2016. After all sitemaps have been parsed, the worker then moves on to begin processesing the url queue for html webpages using HTMLAgilityPack. Any allowed href from a link would be added to the url queue to be crawled later. Error sites such as page not found and more are managed through a collection of error checking conditionals after parsing the web page. 

In the admin web role there were 5 main WebMethods : StartCrawling, StopCrawling, ClearIndex, GetPageTitle, and GetAllStats. Supporting these 5 main webmethods are many private functions including GetErrors, GetLastTen, TableIndexCount, GetTotalUrlsCrawled, and more that contribute statistical information about the crawler to GetAllStats which compiles this information into a JSON string and sends it to the front end. 

A class library was also created to share custom classes between worker and web roles such as the :
Website class, which was used to index the website data
Counter class, which was used to keep track of the table index
Stats class, which kept track of the worker performance (stored on table)
Status class, which kept track of the worker status (stored on table)
