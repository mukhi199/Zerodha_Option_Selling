// One-shot token generator. Exchange request_token → access_token → write cache file.
using KiteConnect;

string apiKey    = "6zfzn8a4x91snnpz";
string apiSecret = "ax08xkr022fiawsy4le2dtewl1lsvszn";
string requestToken = args.Length > 0 ? args[0] : "8UB4hruX62H3Vc7xtuKTEvc7wOKumvEH";

// Token cache paths
string streamerTokenPath  = "../Trading.Streamer/access_token.json";
string strategyTokenPath  = "../Trading.Strategy/access_token.json";

Console.WriteLine($"Exchanging request_token: {requestToken}");

var kite    = new Kite(apiKey, Debug: false);
var session = kite.GenerateSession(requestToken, apiSecret);
var accessToken = session.AccessToken;

Console.WriteLine($"✅ Access Token: {accessToken}");

var content = $"{DateTime.Today:yyyy-MM-dd}|{accessToken}";

// Write to both service directories
File.WriteAllText(streamerTokenPath,  content);
File.WriteAllText(strategyTokenPath,  content);

Console.WriteLine($"Token cached to:");
Console.WriteLine($"  {Path.GetFullPath(streamerTokenPath)}");
Console.WriteLine($"  {Path.GetFullPath(strategyTokenPath)}");
Console.WriteLine("Done! You can now start the services.");
