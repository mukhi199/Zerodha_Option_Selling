using System;
using KiteConnect;

namespace Trading.Tools;

public class TokenExchanger
{
    public static void Main()
    {
        string apiKey = "6zfzn8a4x91snnpz";
        string apiSecret = "ax08xkr022fiawsy4le2dtewl1lsvszn";
        string requestToken = "PbRJv1uO4rPSaflswB9I0tOgdA1i1ZL4";

        var kite = new Kite(apiKey);
        try
        {
            var session = kite.GenerateSession(requestToken, apiSecret);
            Console.WriteLine("--- SESSION GENERATED ---");
            Console.WriteLine($"Access Token: {session.AccessToken}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
        }
    }
}
