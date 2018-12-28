using Shaman.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using Shaman.Dom;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Net.Http;
using Newtonsoft.Json;
using System.Reflection;
using System.Diagnostics;
using System.Text.Utf8;
using System.Globalization;
using Shaman;
using Shaman.Scraping;
using CoreTweet;
using LiteDB;
#if DOM_EMULATION
using Shaman.DomEmulation;
#endif
using System.Threading;

namespace TweetArchiver
{
    class Program
    {

#if DEBUG
        private readonly static bool IsDebug = true;
#else
        private readonly static bool IsDebug = false;
#endif
        static LiteDatabase DB;
        static WebsiteScraper Scraper;
        static OAuth.OAuthSession Session;
        static Tokens Tokens;
        static System.Threading.Timer t;
        static int Main(string[] args)
        {
            ConfigurationManager.Initialize(typeof(Program).GetTypeInfo().Assembly, IsDebug);
            ConfigurationManager.Initialize(typeof(HttpUtils).GetTypeInfo().Assembly, IsDebug);
            ConfigurationManager.Initialize(typeof(SingleThreadSynchronizationContext).GetTypeInfo().Assembly, IsDebug);
            try
            {
                Shaman.Runtime.SingleThreadSynchronizationContext.Run(MainAsync);
            }
            catch (Exception ex)
            {
                var z = ex.RecursiveEnumeration(x => x.InnerException).Last();
                Console.WriteLine(z);
                return 1;
            }
            finally
            {
                BlobStore.CloseAllForShutdown();
            }
            return 0;
        }

        public static async Task MainAsync()
        {
            DB = new LiteDatabase("tweetarchive.db");
            var pin = GetPin();
            Tokens = await OAuth.GetTokensAsync(Session, pin);
            SetupScraper();
            int startin = 60 - DateTime.Now.Second;
            t = new System.Threading.Timer(async o => await Scrape(),
                 null, startin * 1000, 60000);
            while(true)
            {
                // Kill the program to exit
            }
        }

        public static async Task Scrape()
        {
#if DEBUG
            var rateLimit = await Tokens.Application.RateLimitStatusAsync();
            foreach(var limit in rateLimit)
            {
                Debug.WriteLine($"{limit.Key} - {limit.Value}");
            }
#endif
            var mentions = await Tokens.Statuses.MentionsTimelineAsync();
            var mentionIds = mentions.Select(n => n.Id);
            var tweets = DB.GetCollection<ArchiveTweet>("tweets");
            foreach (var mention in mentions)
            {
                var tweet = tweets.Find(n => n.Id == mention.Id);
                if (tweet.Any())
                {
                    Debug.WriteLine($"Skipping {mention.Id}, exists");
                    continue;
                }
                Debug.WriteLine($"Adding {mention.Id}");
                var replyUrl = $"https://twitter.com/{mention.User.ScreenName}/status/{mention.Id}";
                Scraper.AddToCrawl(new Uri(replyUrl));
                tweets.Insert(new ArchiveTweet() { Id = mention.Id,
                    InReplyToStatusId = mention.InReplyToStatusId.HasValue ? mention.InReplyToStatusId.Value : 0,
                    ScreenName = mention.User.ScreenName,
                    Text = mention.Text
                });
            }
            await Scraper.ScrapeAsync();
        }

        public static string GetPin()
        {
            var oauth = JsonConvert.DeserializeObject<TokensOauth>(File.ReadAllText("tokens.json"));
            Session = OAuth.Authorize(oauth.key, oauth.secret);
            System.Diagnostics.Process.Start(Session.AuthorizeUri.AbsoluteUri);
            Console.WriteLine(Session.AuthorizeUri.AbsoluteUri);
            Console.WriteLine("Enter your Twitter Auth pin: ");
            return Console.ReadLine();
        }

        private static void SetupScraper()
        {
            Scraper = new WebsiteScraper();
            Scraper.PerformInitialization();
            Directory.CreateDirectory("WARC");
            Scraper.CreateThreadProgressDelegate = () => Program.CreateSimpleConsoleProgress("Crawler thread", true);
            Scraper.CreateMainProgressDelegate = () => Program.CreateSimpleConsoleProgress("Crawler");
            Console.CancelKeyPress += (s, e) =>
            {
                Scraper.Dispose();
                t.Dispose();
            };
            Scraper.OutputAsWarc = true;
            Scraper.DestinationDirectory = Path.GetFullPath("WARC");
            Scraper.DatabaseSaveInterval = TimeSpan.FromMinutes(1);
            Scraper.ShouldScrape = (url, prereq) =>
            {
                var stringUrl = url.ToString();
                if (stringUrl.Contains(".css") || stringUrl.Contains(".js"))
                    return true;
                if (stringUrl.Contains("lang="))
                    return false;
                if (stringUrl.Contains("mobile.twitter.com") || stringUrl.Contains("publish.twitter.com"))
                    return false;
                if (url.IsHostedOn("twimg.com"))
                    return true;
                if (stringUrl.Contains("/status/"))
                    return true;
                if (prereq)
                    return true;
                return false;
            };
            //Scraper.ReconsiderSkippedUrls();
        }

#if NET46
        public static ConsoleProgress<SimpleProgress> CreateSimpleConsoleProgress(string name, bool small = false)
        {
            var progress = ConsoleProgress.Create<SimpleProgress>(name, (p, c) =>
            {
                if (p.Description != null) c.Report(p.Description);
                else c.Report(p.Done, p.Total);
            });
            progress.Controller.BackgroundColor = Console.BackgroundColor;
            progress.Controller.ForegroundColor = ConsoleColor.Magenta;
            progress.Controller.SmallMode = small;
            return progress;
        }
#else
        public static IProgress<SimpleProgress> CreateSimpleConsoleProgress(string name, bool small = false)
        {
            // TODO
            return null;
        }
#endif
    }

    public class TokensOauth
    {
        public string key { get; set; }

        public string secret { get; set; }
    }

    public class ArchiveTweet
    {
        public long Id { get; set; }

        public long InReplyToStatusId { get; set; }

        public string ScreenName { get; set; }

        public string Text { get; set; }
    }
}
