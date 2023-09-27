/* Forked from https://github.com/Azure-Samples/media-services-video-indexer/tree/master/API-Samples/C%23/ArmBased */

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace VideoIndexer
{
    public class Program
    {

        #region constants

        private const string ApiVersion = "2022-08-01";
        private const string AzureResourceManager = "https://management.azure.com";
        private static string SubscriptionId;
        private static string ResourceGroup;
        private static string AccountName;

         private static string ManagedIdentityClientId;
        private static string TenantId;

        //Choose public Access Video URL
        private const string VideoUrl = "<Your Video Url Here>";
        //OR 
        /// Optional : Use Local File Upload 
        private static string LocalVideoPath;

        private static string VideoFileName;
        private static string VideoIndex;

        private static List<Transcript> Transcripts ;

        private const string ApiUrl = "https://api.videoindexer.ai";
        private const string ExcludedAI = ""; // Enter a list seperated by a comma of the AIs you would like to exclude in the format "<Faces,Labels,Emotions,ObservedPeople>". Leave empty if you do not want to exclude any AIs. For more see here https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Upload-Video:~:text=AI%20to%20exclude%20when%20indexing%2C%20for%20example%20for%20sensitive%20scenarios.%20Options%20are%3A%20Face/Observed%20peopleEmotions/Labels%7D.
#endregion
        public static async Task Main(string[] args)
        {
            #region config
            // Build a config object, using env vars and JSON providers.
            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables()
                .Build();

            SubscriptionId = config["SubscriptionId"];
            ResourceGroup = config["ResourceGroup"];
            AccountName = config["AccountName"];
            ManagedIdentityClientId = config["ManagedIdentityClientId"];
            TenantId = config["TenantId"];
            var rootDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            
            var folderPath = @rootDir + "\\Data\\";

            DirectoryInfo di = new DirectoryInfo(folderPath)  ;
  #endregion

            #region Video and Httpclient
            //Get All mp4 Files  
            var getAllVideoFiles = di.GetFiles("*.mp4")  
                                            .Where(file => file.Name.EndsWith(".mp4"))  
                                            .Select(file => file.FullName).ToList();  
            
            LocalVideoPath = getAllVideoFiles[0];
           VideoFileName= Path.GetFileNameWithoutExtension(LocalVideoPath);
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount();
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account);

            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            // Create the http client
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
                
            };
            var client = new HttpClient(handler);
            client.Timeout = new TimeSpan(10,0,0);
            #endregion

            // Upload a video
            var videoId = await UploadVideo(accountId, accountLocation, accountAccessToken, ApiUrl, client);

            // Wait for the video index to finish and save the transcript
            await WaitForIndexAndSaveTranscript(accountId, accountLocation, accountAccessToken, ApiUrl, client, videoId);
            
            #region  addiitonal funcs not used
            //var videoId = "ggfd";
            // Get video level access token for Azure Video Indexer 
            //var videoAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Video, videoId);

            //GetIndex(accountId, accountLocation, accountAccessToken, ApiUrl, client, videoId);

            // Search for the video
            //await GetVideo(accountId, accountLocation, videoAccessToken, ApiUrl, client, videoId);

            // Get insights widget url
            //await GetInsightsWidgetUrl(accountId, accountLocation, videoAccessToken, ApiUrl, client, videoId);

            // Get player widget url
            //await GetPlayerWidgetUrl(accountId, accountLocation, videoAccessToken, ApiUrl, client, videoId);

            //Console.WriteLine("\nPress Enter to exit...");
            //var line = Console.ReadLine();
            //if (line == "enter")
            //{
             //   System.Environment.Exit(0);
            //}
            #endregion
        }

        /// <summary>
        /// Uploads a video and starts the video index. Calls the uploadVideo API (https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Upload-Video)
        /// </summary>
        /// <param name="accountId"> The account ID</param>
        /// <param name="accountLocation"> The account location </param>
        /// <param name="acountAccessToken"> The access token </param>
        /// <param name="apiUrl"> The video indexer api url </param>
        /// <param name="client"> The http client </param>
        /// <returns> Video Id of the video being indexed, otherwise throws excpetion</returns>
        private static async Task<string> UploadVideo(string accountId, string accountLocation, string acountAccessToken, string apiUrl, HttpClient client)
        {
            Console.WriteLine($"Video for account {accountId} is starting to upload.");
            var content = new MultipartFormDataContent();
            FileStream fileStream = null;
            StreamContent streamContent = null;
            try
            {
                //Build Query Parameter Dictionary
                var queryDictionary = new Dictionary<string, string>
                {
                    { "accessToken", acountAccessToken },
                    { "name", VideoFileName },
                    { "description", "video_description" },
                    { "privacy", "private" },
                    { "partition", "partition" }
                };

                if (!string.IsNullOrEmpty(VideoUrl) && Uri.IsWellFormedUriString(VideoUrl, UriKind.Absolute))
                {
                    Console.WriteLine("Using publiuc video url For upload.");
                    queryDictionary.Add("videoUrl", VideoUrl);
                }
                else if (File.Exists(LocalVideoPath))
                {
                    Console.WriteLine("Using local video Multipart upload.");
                    // Add file content
                    fileStream = new FileStream(LocalVideoPath, FileMode.Open, FileAccess.Read);
                    streamContent = new StreamContent(fileStream);
                    content.Add(streamContent, "fileName", Path.GetFileName(LocalVideoPath));
                    streamContent.Headers.Add("Content-Type", "multipart/form-data");
                    streamContent.Headers.Add("Content-Length", fileStream.Length.ToString());
                }
                else
                {
                    throw new ArgumentException("VideoUrl or LocalVidePath are invalid");
                }
                var queryParams = CreateQueryString(queryDictionary);
                queryParams += AddExcludedAIs(ExcludedAI);

                // Send POST request
                var uploadRequestResult = await client.PostAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}", content);
                VerifyStatus(uploadRequestResult, System.Net.HttpStatusCode.OK);
                var uploadResult = await uploadRequestResult.Content.ReadAsStringAsync();

                // Get the video ID from the upload result
                var videoId = JsonSerializer.Deserialize<Video>(uploadResult).id;
                Console.WriteLine($"\nVideo ID {videoId} was uploaded successfully");
                return videoId;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }
            finally
            {
                fileStream?.Dispose();
                streamContent?.Dispose();
            }
        }



        /// <summary>
        /// Calls getVideoIndex API in 10 second intervals until the indexing state is 'processed'(https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Get-Video-Index)
        /// </summary>
        /// <param name="accountId"> The account ID</param>
        /// <param name="accountLocation"> The account location </param>
        /// <param name="acountAccessToken"> The access token </param>
        /// <param name="apiUrl"> The video indexer api url </param>
        /// <param name="client"> The http client </param>
        /// <param name="videoId"> The video id </param>
        /// <returns> Prints video index when the index is complete, otherwise throws exception </returns>
        private static async Task WaitForIndexAndSaveTranscript(string accountId, string accountLocation, string acountAccessToken, string apiUrl, HttpClient client, string videoId)
        {

            Console.WriteLine($"\nWaiting for video {videoId} to finish indexing.");
            string queryParams;
            while (true)
            {
                queryParams = CreateQueryString(
                    new Dictionary<string, string>()
                    {
                            {"accessToken", acountAccessToken},
                            {"language", "English"},
                    });

                var videoGetIndexRequestResult = await client.GetAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Index?{queryParams}");

                VerifyStatus(videoGetIndexRequestResult, System.Net.HttpStatusCode.OK);
                var videoGetIndexResult = await videoGetIndexRequestResult.Content.ReadAsStringAsync();
                string processingState = JsonSerializer.Deserialize<VideoIndexerInsights>(videoGetIndexResult).state;

                // If job is finished
                if (processingState == ProcessingState.Processed.ToString())
                {
                    Console.WriteLine($"The video index has completed. Here is the full JSON of the index for video ID {videoId}: \n{videoGetIndexResult}");
                    var viIndexres = JsonSerializer.Deserialize<VideoIndexerInsights>(videoGetIndexResult);
                    Transcripts =  new List<Transcript>(viIndexres.videos[0].insights.transcript);

                

                string json = (JsonSerializer.Serialize(Transcripts.Select(i=>new { id=i.id.ToString(),content=i.text  })));
                var jsonCleaned = json.Replace("\\u0027","'");
                //write string to file
                var rootDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var LocalJsonPath = @rootDir + "\\"+videoId+"_"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+".json";
                System.IO.File.WriteAllText(LocalJsonPath, jsonCleaned);
                    return;
                }
                else if (processingState == ProcessingState.Failed.ToString())
                {
                    Console.WriteLine($"\nThe video index failed for video ID {videoId}.");
                    throw new Exception(videoGetIndexResult);
                }

                // Job hasn't finished
                Console.WriteLine($"\nThe video index state is {processingState}");
                await Task.Delay(10000);
            }
        }


        private static async Task GetIndex(string accountId, string accountLocation, string acountAccessToken, string apiUrl, HttpClient client, string videoId)
        {

try{
            Console.WriteLine($"\nWaiting for video {videoId} to finish indexing.");
            string queryParams;
            
                queryParams = CreateQueryString(
                    new Dictionary<string, string>()
                    {
                            {"accessToken", acountAccessToken},
                            {"language", "English"},
                    });

                var videoGetIndexRequestResult = await client.GetAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Index?{queryParams}");

                VerifyStatus(videoGetIndexRequestResult, System.Net.HttpStatusCode.OK);
                var videoGetIndexResult = await videoGetIndexRequestResult.Content.ReadAsStringAsync();
                var viIndexres = JsonSerializer.Deserialize<VideoIndexerInsights>(videoGetIndexResult);
                Transcripts =  new List<Transcript>(viIndexres.videos[0].insights.transcript);

                

                string json = (JsonSerializer.Serialize(Transcripts.Select(i=>new { id=i.id.ToString(),content=i.text })));
                var jsonCleaned = json.Replace("\\u0027","'");
                //write string to file
                var rootDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var LocalJsonPath = @rootDir + "\\"+videoId+"_"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+".json";
                System.IO.File.WriteAllText(LocalJsonPath, jsonCleaned);
}
                catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            
        }

        /// <summary>
        /// Searches for the video in the account. Calls the searchVideo API (https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Search-Videos)
        /// </summary>
        /// <param name="accountId"> The account ID</param>
        /// <param name="accountLocation"> The account location </param>
        /// <param name="videoAccessToken"> The access token </param>
        /// <param name="apiUrl"> The video indexer api url </param>
        /// <param name="client"> The http client </param>
        /// <param name="videoId"> The video id </param>
        /// <returns> Prints the video metadata, otherwise throws excpetion</returns>
        private static async Task GetVideo(string accountId, string accountLocation, string videoAccessToken, string apiUrl, HttpClient client, string videoId)
        {
            Console.WriteLine($"\nSearching videos in account {AccountName} for video ID {videoId}.");
            var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                        {"accessToken", videoAccessToken},
                        {"id", videoId},
                });

            try
            {
                var searchRequestResult = await client.GetAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos/Search?{queryParams}");

                VerifyStatus(searchRequestResult, System.Net.HttpStatusCode.OK);
                var searchResult = await searchRequestResult.Content.ReadAsStringAsync();
                Console.WriteLine($"Here are the search results: \n{searchResult}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// Calls the getVideoInsightsWidget API (https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Get-Video-Insights-Widget)
        /// </summary>
        /// <param name="accountId"> The account ID</param>
        /// <param name="accountLocation"> The account location </param>
        /// <param name="videoAccessToken"> The access token </param>
        /// <param name="apiUrl"> The video indexer api url </param>
        /// <param name="client"> The http client </param>
        /// <param name="videoId"> The video id </param>
        /// <returns> Prints the VideoInsightsWidget URL, otherwise throws exception</returns>
        private static async Task GetInsightsWidgetUrl(string accountId, string accountLocation, string videoAccessToken, string apiUrl, HttpClient client, string videoId)
        {
            Console.WriteLine($"\nGetting the insights widget URL for video {videoId}");
            var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    {"accessToken", videoAccessToken},
                    {"widgetType", "Keywords"},
                    {"allowEdit", "true"},
                });
            try
            {
                var insightsWidgetRequestResult = await client.GetAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/InsightsWidget?{queryParams}");

                VerifyStatus(insightsWidgetRequestResult, System.Net.HttpStatusCode.MovedPermanently);
                var insightsWidgetLink = insightsWidgetRequestResult.Headers.Location;
                Console.WriteLine($"Got the insights widget URL: \n{insightsWidgetLink}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// Calls the getVideoPlayerWidget API (https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Get-Video-Player-Widget)
        /// </summary>
        /// <param name="accountId"> The account ID</param>
        /// <param name="accountLocation"> The account location </param>
        /// <param name="videoAccessToken"> The access token </param>
        /// <param name="apiUrl"> The video indexer api url </param>
        /// <param name="client"> The http client </param>
        /// <param name="videoId"> The video id </param>
        /// <returns> Prints the VideoPlayerWidget URL, otherwise throws exception</returns>
        private static async Task GetPlayerWidgetUrl(string accountId, string accountLocation, string videoAccessToken, string apiUrl, HttpClient client, string videoId)
        {
            Console.WriteLine($"\nGetting the player widget URL for video {videoId}");
            var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    {"accessToken", videoAccessToken},
                });

            try
            {
                var playerWidgetRequestResult = await client.GetAsync($"{apiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/PlayerWidget?{queryParams}");

                var playerWidgetLink = playerWidgetRequestResult.Headers.Location;
                VerifyStatus(playerWidgetRequestResult, System.Net.HttpStatusCode.MovedPermanently);
                Console.WriteLine($"Got the player widget URL: \n{playerWidgetLink}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        static string CreateQueryString(IDictionary<string, string> parameters)
        {
            var queryParameters = HttpUtility.ParseQueryString(string.Empty);
            foreach (var parameter in parameters)
            {
                queryParameters[parameter.Key] = parameter.Value;
            }

            return queryParameters.ToString();
        }

        static string AddExcludedAIs(string excludedAI)
        {
            if (String.IsNullOrEmpty(excludedAI))
            {
                return "";
            }
            var list = excludedAI.Split(',');
            var result = "";
            foreach (var item in list)
            {
                result += "&excludedAI=" + item;
            }
            return result;
        }

        public class VideoIndexerResourceProviderClient
        {
            private readonly string armAccessToken;

            async public static Task<VideoIndexerResourceProviderClient> BuildVideoIndexerResourceProviderClient()
            {
                var tokenRequestContext = new TokenRequestContext(new[] { $"{AzureResourceManager}/.default" });
                //var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions{ManagedIdentityClientId="",TenantId=""}).GetTokenAsync(tokenRequestContext);
                var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions{ManagedIdentityClientId=ManagedIdentityClientId,TenantId=TenantId}).GetTokenAsync(tokenRequestContext);
                return new VideoIndexerResourceProviderClient(tokenRequestResult.Token);
            }
            public VideoIndexerResourceProviderClient(string armAaccessToken)
            {
                this.armAccessToken = armAaccessToken;
            }

            /// <summary>
            /// Generates an access token. Calls the generateAccessToken API  (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D/generateAccessToken%22%3A%20%7B)
            /// </summary>
            /// <param name="permission"> The permission for the access token</param>
            /// <param name="scope"> The scope of the access token </param>
            /// <param name="videoId"> if the scope is video, this is the video Id </param>
            /// <param name="projectId"> If the scope is project, this is the project Id </param>
            /// <returns> The access token, otherwise throws an exception</returns>
            public async Task<string> GetAccessToken(ArmAccessTokenPermission permission, ArmAccessTokenScope scope, string videoId = null, string projectId = null)
            {
                var accessTokenRequest = new AccessTokenRequest
                {
                    PermissionType = permission,
                    Scope = scope,
                    VideoId = videoId,
                    ProjectId = projectId
                };

                Console.WriteLine($"\nGetting access token: {JsonSerializer.Serialize(accessTokenRequest)}");

                // Set the generateAccessToken (from video indexer) http request content
                try
                {
                    var jsonRequestBody = JsonSerializer.Serialize(accessTokenRequest);
                    var httpContent = new StringContent(jsonRequestBody, System.Text.Encoding.UTF8, "application/json");

                    // Set request uri
                    var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}/generateAccessToken?api-version={ApiVersion}";
                    var client = new HttpClient(new HttpClientHandler());
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                    var result = await client.PostAsync(requestUri, httpContent);

                    VerifyStatus(result, System.Net.HttpStatusCode.OK);
                    var jsonResponseBody = await result.Content.ReadAsStringAsync();
                    Console.WriteLine($"Got access token: {scope} {videoId}, {permission}");
                    return JsonSerializer.Deserialize<GenerateAccessTokenResponse>(jsonResponseBody).AccessToken;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    throw;
                }
            }

            /// <summary>
            /// Gets an account. Calls the getAccount API (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D%22%3A%20%7B)
            /// </summary>
            /// <returns> The Account, otherwise throws an exception</returns>
            public async Task<Account> GetAccount()
            {
                Console.WriteLine($"Getting account {AccountName}.");
                Account account;
                try
                {
                    // Set request uri
                    var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}?api-version={ApiVersion}";
                    var client = new HttpClient(new HttpClientHandler());
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                    var result = await client.GetAsync(requestUri);

                    VerifyStatus(result, System.Net.HttpStatusCode.OK);
                    var jsonResponseBody = await result.Content.ReadAsStringAsync();
                    account = JsonSerializer.Deserialize<Account>(jsonResponseBody);
                    VerifyValidAccount(account);
                    Console.WriteLine($"The account ID is {account.Properties.Id}");
                    Console.WriteLine($"The account location is {account.Location}");
                    return account;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    throw;
                }
            }

            private static void VerifyValidAccount(Account account)
            {
                if (string.IsNullOrWhiteSpace(account.Location) || account.Properties == null || string.IsNullOrWhiteSpace(account.Properties.Id))
                {
                    Console.WriteLine($"{nameof(AccountName)} {AccountName} not found. Check {nameof(SubscriptionId)}, {nameof(ResourceGroup)}, {nameof(AccountName)} ar valid.");
                    throw new Exception($"Account {AccountName} not found.");
                }
            }
        }

        public class AccessTokenRequest
        {
            [JsonPropertyName("permissionType")]
            public ArmAccessTokenPermission PermissionType { get; set; }

            [JsonPropertyName("scope")]
            public ArmAccessTokenScope Scope { get; set; }

            [JsonPropertyName("projectId")]
            public string ProjectId { get; set; }

            [JsonPropertyName("videoId")]
            public string VideoId { get; set; }
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum ArmAccessTokenPermission
        {
            Reader,
            Contributor,
            MyAccessAdministrator,
            Owner,
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum ArmAccessTokenScope
        {
            Account,
            Project,
            Video
        }

        public class GenerateAccessTokenResponse
        {
            [JsonPropertyName("accessToken")]
            public string AccessToken { get; set; }
        }

        public class AccountProperties
        {
            [JsonPropertyName("accountId")]
            public string Id { get; set; }
        }

        public class Account
        {
            [JsonPropertyName("properties")]
            public AccountProperties Properties { get; set; }

            [JsonPropertyName("location")]
            public string Location { get; set; }
        }

      
        public enum ProcessingState
        {
            Uploaded,
            Processing,
            Processed,
            Failed
        }

        
public class VideoIndexerInsights
{
    public string partition { get; set; }
    public string description { get; set; }
    public string privacyMode { get; set; }
    public string state { get; set; }
    public string accountId { get; set; }
    public string id { get; set; }
    public string name { get; set; }
    public string userName { get; set; }
    public DateTime created { get; set; }
    public bool isOwned { get; set; }
    public bool isEditable { get; set; }
    public bool isBase { get; set; }
    public int durationInSeconds { get; set; }
    public string duration { get; set; }
    public object summarizedInsights { get; set; }
    public Video[] videos { get; set; }
    public Videosrange[] videosRanges { get; set; }
}

public class Video
{
    public string accountId { get; set; }
    public string id { get; set; }
    public string state { get; set; }
    public string moderationState { get; set; }
    public string reviewState { get; set; }
    public string privacyMode { get; set; }
    public string processingProgress { get; set; }
    public string failureMessage { get; set; }
    public object externalId { get; set; }
    public object externalUrl { get; set; }
    public object metadata { get; set; }
    public Insights insights { get; set; }
    public string thumbnailId { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public bool detectSourceLanguage { get; set; }
    public string languageAutoDetectMode { get; set; }
    public string sourceLanguage { get; set; }
    public string[] sourceLanguages { get; set; }
    public string language { get; set; }
    public string[] languages { get; set; }
    public string indexingPreset { get; set; }
    public string streamingPreset { get; set; }
    public string linguisticModelId { get; set; }
    public string personModelId { get; set; }
    public object logoGroupId { get; set; }
    public bool isAdult { get; set; }
    public string publishedUrl { get; set; }
    public object publishedProxyUrl { get; set; }
    public string viewToken { get; set; }
}

public class Insights
{
    public string version { get; set; }
    public string duration { get; set; }
    public string sourceLanguage { get; set; }
    public string[] sourceLanguages { get; set; }
    public string language { get; set; }
    public string[] languages { get; set; }
    public Transcript[] transcript { get; set; }
    public Ocr[] ocr { get; set; }
    public Keyword[] keywords { get; set; }
    public Topic[] topics { get; set; }
    public Face[] faces { get; set; }
    public Label[] labels { get; set; }
    public Scene[] scenes { get; set; }
    public Shot[] shots { get; set; }
    public Brand[] brands { get; set; }
    public Namedlocation[] namedLocations { get; set; }
    public Namedpeople[] namedPeople { get; set; }
    public Audioeffect[] audioEffects { get; set; }
    public Detectedobject[] detectedObjects { get; set; }
    public Sentiment[] sentiments { get; set; }
    public Emotion[] emotions { get; set; }
    public Block[] blocks { get; set; }
    public Framepattern[] framePatterns { get; set; }
    public Speaker[] speakers { get; set; }
    public Textualcontentmoderation textualContentModeration { get; set; }
    public Statistics statistics { get; set; }
}

public class Textualcontentmoderation
{
    public int id { get; set; }
    public int bannedWordsCount { get; set; }
    public int bannedWordsRatio { get; set; }
    public object[] instances { get; set; }
}

public class Statistics
{
    public int correspondenceCount { get; set; }
    public Speakertalktolistenratio speakerTalkToListenRatio { get; set; }
    public Speakerlongestmonolog speakerLongestMonolog { get; set; }
    public Speakernumberoffragments speakerNumberOfFragments { get; set; }
    public Speakerwordcount speakerWordCount { get; set; }
}

public class Speakertalktolistenratio
{
    public float _1 { get; set; }
    public float _2 { get; set; }
    public float _3 { get; set; }
}

public class Speakerlongestmonolog
{
    public int _1 { get; set; }
    public int _2 { get; set; }
    public int _3 { get; set; }
}

public class Speakernumberoffragments
{
    public int _1 { get; set; }
    public int _2 { get; set; }
    public int _3 { get; set; }
}

public class Speakerwordcount
{
    public int _1 { get; set; }
    public int _2 { get; set; }
    public int _3 { get; set; }
}

public class Transcript
{
    public int id { get; set; }
    public string text { get; set; }
    public float confidence { get; set; }
    public int speakerId { get; set; }
    public string language { get; set; }
    public Instance[] instances { get; set; }
}

public class Instance
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Ocr
{
    public int id { get; set; }
    public string text { get; set; }
    public float confidence { get; set; }
    public int left { get; set; }
    public int top { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public int angle { get; set; }
    public string language { get; set; }
    public Instance1[] instances { get; set; }
}

public class Instance1
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Keyword
{
    public int id { get; set; }
    public string text { get; set; }
    public float confidence { get; set; }
    public string language { get; set; }
    public Instance2[] instances { get; set; }
}

public class Instance2
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Topic
{
    public int id { get; set; }
    public string name { get; set; }
    public string referenceId { get; set; }
    public string referenceType { get; set; }
    public string iptcName { get; set; }
    public float confidence { get; set; }
    public string iabName { get; set; }
    public string language { get; set; }
    public Instance3[] instances { get; set; }
    public string referenceUrl { get; set; }
}

public class Instance3
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Face
{
    public int id { get; set; }
    public string name { get; set; }
    public int confidence { get; set; }
    public object description { get; set; }
    public string thumbnailId { get; set; }
    public object title { get; set; }
    public object imageUrl { get; set; }
    public Thumbnail[] thumbnails { get; set; }
    public Instance5[] instances { get; set; }
}

public class Thumbnail
{
    public string id { get; set; }
    public string fileName { get; set; }
    public Instance4[] instances { get; set; }
}

public class Instance4
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Instance5
{
    public string[] thumbnailsIds { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Label
{
    public int id { get; set; }
    public string name { get; set; }
    public string referenceId { get; set; }
    public string language { get; set; }
    public Instance6[] instances { get; set; }
}

public class Instance6
{
    public float confidence { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Scene
{
    public int id { get; set; }
    public Instance7[] instances { get; set; }
}

public class Instance7
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Shot
{
    public int id { get; set; }
    public Keyframe[] keyFrames { get; set; }
    public Instance9[] instances { get; set; }
    public string[] tags { get; set; }
}

public class Keyframe
{
    public int id { get; set; }
    public Instance8[] instances { get; set; }
}

public class Instance8
{
    public string thumbnailId { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Instance9
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Brand
{
    public int id { get; set; }
    public string referenceType { get; set; }
    public string name { get; set; }
    public string referenceId { get; set; }
    public string referenceUrl { get; set; }
    public string description { get; set; }
    public object[] tags { get; set; }
    public float confidence { get; set; }
    public bool isCustom { get; set; }
    public Instance10[] instances { get; set; }
}

public class Instance10
{
    public string brandType { get; set; }
    public string instanceSource { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Namedlocation
{
    public int id { get; set; }
    public string name { get; set; }
    public object referenceId { get; set; }
    public object referenceUrl { get; set; }
    public object description { get; set; }
    public object[] tags { get; set; }
    public float confidence { get; set; }
    public bool isCustom { get; set; }
    public Instance11[] instances { get; set; }
}

public class Instance11
{
    public string instanceSource { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Namedpeople
{
    public int id { get; set; }
    public string name { get; set; }
    public string referenceId { get; set; }
    public string referenceUrl { get; set; }
    public string description { get; set; }
    public object[] tags { get; set; }
    public float confidence { get; set; }
    public bool isCustom { get; set; }
    public Instance12[] instances { get; set; }
}

public class Instance12
{
    public string instanceSource { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Audioeffect
{
    public int id { get; set; }
    public string type { get; set; }
    public Instance13[] instances { get; set; }
}

public class Instance13
{
    public float confidence { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Detectedobject
{
    public int id { get; set; }
    public string type { get; set; }
    public string thumbnailId { get; set; }
    public string displayName { get; set; }
    public string wikiDataId { get; set; }
    public Instance14[] instances { get; set; }
}

public class Instance14
{
    public float confidence { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Sentiment
{
    public int id { get; set; }
    public float averageScore { get; set; }
    public string sentimentType { get; set; }
    public Instance15[] instances { get; set; }
}

public class Instance15
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Emotion
{
    public int id { get; set; }
    public string type { get; set; }
    public Instance16[] instances { get; set; }
}

public class Instance16
{
    public float confidence { get; set; }
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Block
{
    public int id { get; set; }
    public Instance17[] instances { get; set; }
}

public class Instance17
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Framepattern
{
    public int id { get; set; }
    public string patternType { get; set; }
    public int confidence { get; set; }
    public object displayName { get; set; }
    public Instance18[] instances { get; set; }
}

public class Instance18
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Speaker
{
    public int id { get; set; }
    public string name { get; set; }
    public Instance19[] instances { get; set; }
}

public class Instance19
{
    public string adjustedStart { get; set; }
    public string adjustedEnd { get; set; }
    public string start { get; set; }
    public string end { get; set; }
}

public class Videosrange
{
    public string videoId { get; set; }
    public Range range { get; set; }
}

public class Range
{
    public string start { get; set; }
    public string end { get; set; }
}


        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode excpectedStatusCode)
        {
            if (response.StatusCode != excpectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }
    }
}