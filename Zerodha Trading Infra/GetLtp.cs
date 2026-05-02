using KiteConnect;
using System.IO;
using System;

string apiKey = "6zfzn8a4x91snnpz";
string tokenPath = "Trading.Strategy/access_token.json";
var raw = File.ReadAllText(tokenPath).Split('|');
string accessToken = raw[1];

var kite = new Kite(apiKey);
kite.SetAccessToken(accessToken);

string[] symbols = new[] { 
    "NFO:NIFTY26APR24200CE", 
    "NFO:NIFTY26APR23800PE", 
    "NFO:BANKNIFTY26APR56800CE", 
    "NFO:BANKNIFTY26APR55800PE",
    "NFO:NIFTY26APRFUT",
    "NFO:NIFTY26APR22850PE"
};

var ltps = kite.GetLTP(symbols);

foreach (var sym in symbols)
{
    if (ltps.ContainsKey(sym))
    {
        Console.WriteLine($"{sym}|{ltps[sym].LastPrice}");
    }
}
