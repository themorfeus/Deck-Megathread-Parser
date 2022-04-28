using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DeckFetcher;

public class DeckFetcher
{
    private string AppID;
    private string AppSecret;
    private string AppAuthKey => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{AppID}:{AppSecret}"));

    private Ini ConfigFile;
    private readonly HttpClient Client;
    private string OAuthToken;

    public DeckFetcher()
    {
        Client = new HttpClient();

        ConfigFile = new Ini("config.ini");

        AppID = ConfigFile.GetValue("AppID");
        AppSecret = ConfigFile.GetValue("AppSecret");
        
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", AppAuthKey);
        Client.DefaultRequestHeaders.Add("User-Agent", "steamdeck-fetcher/0.1 by themorfeus");
    }

    public async Task Run()
    {
        int retryCounter = 10;
        OAuthToken = null;
        do
        {
            OAuthToken = await LoginAndGetToken();

            if (OAuthToken == null)
            {
                Console.WriteLine("Invalid Login\n");
                retryCounter--;
            }
        } while (OAuthToken == null && retryCounter > 0);

        if (OAuthToken == null)
        {
            Console.WriteLine("Login Failed");
            Shutdown();
            return;
        }
        
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OAuthToken);
        
        string postID;
        do
        {
            postID = GetPostID();
        } while (postID == null);

        var comments = await GetAllComments(postID);

        Console.Write("Enter region keyword (EU, US, UK): ");
        var region = Console.ReadLine().ToLowerInvariant();
        
        Console.Write("Enter size (64, 256, 512): ");
        var size = Console.ReadLine().ToLowerInvariant();
        
        var relevant = FindRelevantComments(comments, region, size);

        var times = FindReserveTimes(relevant).OrderByDescending(x => x.ReserveTime).ToList();
        
        var b = new StringBuilder();
        foreach (var t in times)
        {
            b.AppendLine(t.ToString());
        }
        
        Console.WriteLine("Found Reserve Times: ");
        Console.WriteLine(b.ToString());
        
        Console.WriteLine();
        
        Console.WriteLine($"= HIGHEST RESERVE TIME FOR KEYWORDS: {times.FirstOrDefault()} =");
        Shutdown();
    }

#region Login
    private async Task<string> LoginAndGetToken()
    {
        // seems to work without credentials? uncomment below if doesn't
        
        // var username = string.Empty;
        // var password = string.Empty;
        // do
        // {
        //     Console.Write("Reddit Username: ");
        //     username = Console.ReadLine();
        // } while (string.IsNullOrWhiteSpace(username));
        //
        // do
        // {
        //     Console.Write("Reddit Password: ");
        //     ConsoleKeyInfo keyInfo;
        //     do
        //     {
        //         keyInfo = Console.ReadKey(true);
        //         if (keyInfo.Key == ConsoleKey.Backspace && password.Length > 0)
        //         {
        //             password = password[0..^1];
        //         }
        //         else if (!char.IsControl(keyInfo.KeyChar))
        //         {
        //             password += keyInfo.KeyChar;
        //         }
        //     } while (keyInfo.Key != ConsoleKey.Enter);
        //     Console.WriteLine();
        // } while (string.IsNullOrWhiteSpace(password));

        Console.WriteLine("Fetching OAuth Key...");
        var tokenResponse = await FetchOAuthToken();//username.Trim(), password);

        return tokenResponse.Successful ? tokenResponse.AuthToken : null;
    }

    private async Task<TokenResponse> FetchOAuthToken(string username = null, string password = null)
    {
        var parameters = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
        };

        if (username != null)
        {
            parameters.Add("username", username);
        }
        
        if (password != null)
        {
            parameters.Add("password", password);
        }
        
        var response = await Client.PostAsync("https://www.reddit.com/api/v1/access_token", new FormUrlEncodedContent(parameters));

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);

        return values.ContainsKey("access_token") ? new TokenResponse(true, values["access_token"]) : new TokenResponse(false);
    }
#endregion

    private string GetPostID()
    {
        Console.Write("Enter Post ID: ");
        return Console.ReadLine()?.Trim();
    }
    
    private async Task<string> FetchComments(string postID, string sort = "old", bool threaded = false)
    {
        var url = $"https://oauth.reddit.com/comments/{postID}?sort={sort}&threaded={threaded}&api_type=json&raw_json=1";
        return await Client.GetStringAsync(url);
    }

    private async Task<List<Comment>> GetAllComments(string postID)
    {
        var response = await FetchComments(postID);
        var thread = JArray.Parse(response);

        var moreList = new List<string>();
        var commentBodies = new List<Comment>();

        var data = thread.Last.Last.First["children"];

        ExtractComments(data, commentBodies, moreList);

        var additional = await GetMoreChildren(postID, string.Join(",", moreList));
        
        ExtractComments(additional, commentBodies, moreList);
        
        return commentBodies;
    }

    private void ExtractComments(JToken? data, List<Comment> commentBodies, List<string> moreList)
    {
        foreach (var child in data)
        {
            if (child["kind"]?.ToString() == "more")
            {
                if (child["data"].HasValues)
                {
                    var children = child["data"]["children"];
                    if (children != null)
                    {
                        foreach (var more in children)
                        {
                            moreList.Add(more.ToString());
                        }
                    }
                }
            }
            if (child["kind"]?.ToString() != "t1") continue;
            var body = child["data"]?["body"]?.ToString();
            if (body == null) continue;

            var author = child["data"]["author"].ToString();
            // ignore the recap table
            if (author.ToString().ToLowerInvariant() == "fammy") continue;
            body = Regex.Replace(body, @"[^\u0000-\u007F]+", string.Empty).ToLowerInvariant();

            var link = child["data"]["permalink"].ToString();
            
            var comment = new Comment();

            comment.Body = body;
            comment.Username = author;
            comment.Link = link;
            
            commentBodies.Add(comment);
        }
    }
    

    private async Task<JToken?> GetMoreChildren(string postID, string children)
    {
        var url = $"https://oauth.reddit.com/api/morechildren?link_id=t3_{postID}&children={children}&api_type=json";
        var response = await Client.GetStringAsync(url);
        var thread = JObject.Parse(response);
        return thread["json"]["data"]["things"];
    }
    
    private List<Comment> FindRelevantComments(List<Comment> comments, string region, string size)
    {
        return comments.Where(x => Regex.IsMatch(
            x.Body, 
            @$"((?<=\W|^){region}\W*|{size}).*((?<=\W|^){region}\W*|{size})", 
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant))
            .ToList();
    }

    private List<Comment> FindReserveTimes(List<Comment> relevantComments)
    {
        var reserveTimes = new List<Comment>();
        foreach (var comment in relevantComments)
        {
            var time = Regex.Match(comment.Body, "[0-9]{10}").Value;

            if (int.TryParse(time, out var timeAsInt))
            {
                var newComment = comment;
                newComment.ReserveTime = timeAsInt;
                reserveTimes.Add(newComment);
            }
        }

        return reserveTimes;
    }
    
    private void Shutdown()
    {
        Console.WriteLine("Press any key to quit");
        Console.ReadKey(true);
    }
}

internal struct TokenResponse
{
    public readonly bool Successful;
    public readonly string AuthToken;

    public TokenResponse(bool successful, string authToken = null)
    {
        Successful = successful;
        AuthToken = authToken;
    }
}

internal struct Comment
{
    public string Body;
    public string Username;
    public string Link;
    public int ReserveTime;

    public override string ToString()
    {
        return $"{ReserveTime} by {Username} > https://www.reddit.com{Link}";
    }
}