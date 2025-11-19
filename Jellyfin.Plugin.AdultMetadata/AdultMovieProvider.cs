using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdultMetadata.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdultMetadata
{
    public class AdultMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IMetadataProvider<Movie>
    {
        private readonly ILogger<AdultMovieProvider> _logger;
        private readonly PluginConfiguration _config;

        private readonly HttpClientHandler _handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        private readonly HttpClient _client;

        public AdultMovieProvider(ILogger<AdultMovieProvider> logger, PluginConfiguration config)
        {
            _logger = logger;
            _config = config;
            _client = new HttpClient(_handler);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        }

        public string Name => "Adult Metadata Provider";

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();

            if (_config.EnableGeviProvider)
            {
                // Add cookie for GEVI age-gate bypass
                _handler.CookieContainer.Add(new Uri("https://gayeroticvideoindex.com"), new Cookie("entered", (DateTimeOffset.Now.ToUnixTimeMilliseconds() + 86400000 * 2).ToString()));
                results.AddRange(await SearchGevi(searchInfo.Name, cancellationToken));
            }

            if (_config.EnableAebnProvider)
            {
                // First, bypass AEBN age-gate
                await _client.GetAsync("https://gay.aebn.com/avs/gate-redirect?f=%2Fgay", cancellationToken);
                results.AddRange(await SearchAebn(searchInfo.Name, cancellationToken));
            }

            return results;
        }

        private async Task<List<RemoteSearchResult>> SearchGevi(string query, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            var url = $"https://gayeroticvideoindex.com/search?type=t&where=b&query={Uri.EscapeDataString(query)}";

            try
            {
                var response = await _client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                if (HandleAgeGateInResponse(html))
                {
                    _logger.LogWarning("GEVI age-gate detected, skipping.");
                    return results;
                }

                var anchors = Regex.Matches(html, @"<a[^>]*href=""([^""]*)""[^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
                foreach (Match match in anchors)
                {
                    var href = match.Groups[1].Value;
                    var text = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();

                    if (!string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(text) && href.Contains("/movie/"))
                    {
                        var fullUrl = href.StartsWith("http") ? href : "https://gayeroticvideoindex.com" + href;
                        results.Add(new RemoteSearchResult
                        {
                            Name = text,
                            ProviderIds = new Dictionary<string, string> { { Name, fullUrl } },
                            SearchProviderName = Name
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching GEVI");
            }

            return results;
        }

        private async Task<List<RemoteSearchResult>> SearchAebn(string query, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            var url = $"https://gay.aebn.com/dispatcher/search?type=t&where=b&query={Uri.EscapeDataString(query)}";

            try
            {
                var response = await _client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                if (HandleAgeGateInResponse(html))
                {
                    _logger.LogWarning("AEBN age-gate detected, skipping.");
                    return results;
                }

                var anchors = Regex.Matches(html, @"<a[^>]*href=""([^""]*)""[^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
                foreach (Match match in anchors)
                {
                    var href = match.Groups[1].Value;
                    var text = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();

                    if (!string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(text) && href.Contains("/movie/"))
                    {
                        var fullUrl = href.StartsWith("http") ? href : "https://gay.aebn.com" + href;
                        results.Add(new RemoteSearchResult
                        {
                            Name = text,
                            ProviderIds = new Dictionary<string, string> { { Name, fullUrl } },
                            SearchProviderName = Name
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching AEBN");
            }

            return results;
        }

        private bool HandleAgeGateInResponse(string html)
        {
            // Check for common age-gate phrases
            var ageGateIndicators = new[]
            {
                "age confirmation required",
                "terms of service",
                "enter - i am 18 years of age or older",
                "warning!",
                "this site offers sexually explicit adult content"
            };

            return ageGateIndicators.Any(indicator => html.IndexOf(indicator, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();

            if (info.ProviderIds.TryGetValue(Name, out var url))
            {
                try
                {
                    var response = await _client.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (HandleAgeGateInResponse(html))
                    {
                        _logger.LogWarning("Age-gate detected in metadata fetch.");
                        return result;
                    }

                    // Parse metadata from HTML
                    var titleMatch = Regex.Match(html, @"<title>([^<]*)</title>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success)
                    {
                        result.Item = new Movie { Name = WebUtility.HtmlDecode(titleMatch.Groups[1].Value) };
                    }

                    // Add more parsing as needed
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching metadata");
                }
            }

            return result;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _client.GetAsync(url, cancellationToken);
        }
    }
}