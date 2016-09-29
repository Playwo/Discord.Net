﻿#pragma warning disable CS1591
using Discord.API.Rest;
using Discord.Net;
using Discord.Net.Converters;
using Discord.Net.Queue;
using Discord.Net.Rest;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Discord.API
{
    public class DiscordRestApiClient : IDisposable
    {
        public event Func<string, string, double, Task> SentRequest { add { _sentRequestEvent.Add(value); } remove { _sentRequestEvent.Remove(value); } }
        private readonly AsyncEvent<Func<string, string, double, Task>> _sentRequestEvent = new AsyncEvent<Func<string, string, double, Task>>();
        
        protected readonly JsonSerializer _serializer;
        protected readonly SemaphoreSlim _stateLock;
        private readonly RestClientProvider _restClientProvider;
        private readonly string _userAgent;

        protected string _authToken;
        protected bool _isDisposed;
        private CancellationTokenSource _loginCancelToken;
        private IRestClient _restClient;

        public LoginState LoginState { get; private set; }
        public TokenType AuthTokenType { get; private set; }
        public User CurrentUser { get; private set; }
        public RequestQueue RequestQueue { get; private set; }

        public DiscordRestApiClient(RestClientProvider restClientProvider, string userAgent, JsonSerializer serializer = null, RequestQueue requestQueue = null)
        {
            _restClientProvider = restClientProvider;
            _userAgent = userAgent;
            _serializer = serializer ?? new JsonSerializer { ContractResolver = new DiscordContractResolver() };
            RequestQueue = requestQueue;

            _stateLock = new SemaphoreSlim(1, 1);

            SetBaseUrl(DiscordConfig.ClientAPIUrl);
        }
        internal void SetBaseUrl(string baseUrl)
        {
            _restClient = _restClientProvider(baseUrl);
            _restClient.SetHeader("accept", "*/*");
            _restClient.SetHeader("user-agent", _userAgent);
            _restClient.SetHeader("authorization", GetPrefixedToken(AuthTokenType, _authToken));
        }
        internal static string GetPrefixedToken(TokenType tokenType, string token)
        {
            switch (tokenType)
            {
                case TokenType.Bot:
                    return $"Bot {token}";
                case TokenType.Bearer:
                    return $"Bearer {token}";
                case TokenType.User:
                    return token;
                default:
                    throw new ArgumentException("Unknown OAuth token type", nameof(tokenType));
            }
        }
        internal virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _loginCancelToken?.Dispose();
                    (_restClient as IDisposable)?.Dispose();
                }
                _isDisposed = true;
            }
        }
        public void Dispose() => Dispose(true);

        public async Task LoginAsync(TokenType tokenType, string token, RequestOptions options = null)
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await LoginInternalAsync(tokenType, token, options).ConfigureAwait(false);
            }
            finally { _stateLock.Release(); }
        }
        private async Task LoginInternalAsync(TokenType tokenType, string token, RequestOptions options = null)
        {
            if (LoginState != LoginState.LoggedOut)
                await LogoutInternalAsync().ConfigureAwait(false);
            LoginState = LoginState.LoggingIn;

            try
            {
                _loginCancelToken = new CancellationTokenSource();

                AuthTokenType = TokenType.User;
                _authToken = null;
                await RequestQueue.SetCancelTokenAsync(_loginCancelToken.Token).ConfigureAwait(false);
                _restClient.SetCancelToken(_loginCancelToken.Token);

                AuthTokenType = tokenType;
                _authToken = token;
                _restClient.SetHeader("authorization", GetPrefixedToken(AuthTokenType, _authToken));

                CurrentUser = await GetMyUserAsync(new RequestOptions { IgnoreState = true });

                LoginState = LoginState.LoggedIn;
            }
            catch (Exception)
            {
                await LogoutInternalAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task LogoutAsync()
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await LogoutInternalAsync().ConfigureAwait(false);
            }
            finally { _stateLock.Release(); }
        }
        private async Task LogoutInternalAsync()
        {
            //An exception here will lock the client into the unusable LoggingOut state, but that's probably fine since our client is in an undefined state too.
            if (LoginState == LoginState.LoggedOut) return;
            LoginState = LoginState.LoggingOut;

            try { _loginCancelToken?.Cancel(false); }
            catch { }

            await DisconnectInternalAsync().ConfigureAwait(false);
            await RequestQueue.ClearAsync().ConfigureAwait(false);

            await RequestQueue.SetCancelTokenAsync(CancellationToken.None).ConfigureAwait(false);
            _restClient.SetCancelToken(CancellationToken.None);

            CurrentUser = null;
            LoginState = LoginState.LoggedOut;
        }

        internal virtual Task ConnectInternalAsync() => Task.CompletedTask;
        internal virtual Task DisconnectInternalAsync() => Task.CompletedTask;

        //Core
        public async Task SendAsync(string method, string endpoint, RequestOptions options = null)
        {
            options.HeaderOnly = true;
            var request = new RestRequest(_restClient, method, endpoint, options);
            await SendInternalAsync(method, endpoint, request).ConfigureAwait(false);
        }
        public async Task SendJsonAsync(string method, string endpoint, object payload, RequestOptions options = null)
        {
            options.HeaderOnly = true;
            var json = payload != null ? SerializeJson(payload) : null;
            var request = new JsonRestRequest(_restClient, method, endpoint, json, options);
            await SendInternalAsync(method, endpoint, request).ConfigureAwait(false);
        }
        public async Task SendMultipartAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartArgs, RequestOptions options = null)
        {
            options.HeaderOnly = true;
            var request = new MultipartRestRequest(_restClient, method, endpoint, multipartArgs, options);
            await SendInternalAsync(method, endpoint, request).ConfigureAwait(false);
        }
        public async Task<TResponse> SendAsync<TResponse>(string method, string endpoint, RequestOptions options = null) where TResponse : class
        {
            var request = new RestRequest(_restClient, method, endpoint, options);
            return DeserializeJson<TResponse>(await SendInternalAsync(method, endpoint, request).ConfigureAwait(false));
        }
        public async Task<TResponse> SendJsonAsync<TResponse>(string method, string endpoint, object payload, RequestOptions options = null) where TResponse : class
        {
            var json = payload != null ? SerializeJson(payload) : null;
            var request = new JsonRestRequest(_restClient, method, endpoint, json, options);
            return DeserializeJson<TResponse>(await SendInternalAsync(method, endpoint, request).ConfigureAwait(false));
        }
        public async Task<TResponse> SendMultipartAsync<TResponse>(string method, string endpoint, IReadOnlyDictionary<string, object> multipartArgs, RequestOptions options = null)
        {
            var request = new MultipartRestRequest(_restClient, method, endpoint, multipartArgs, options);
            return DeserializeJson<TResponse>(await SendInternalAsync(method, endpoint, request).ConfigureAwait(false));
        }
        
        private async Task<Stream> SendInternalAsync(string method, string endpoint, RestRequest request)
        {
            if (!request.Options.IgnoreState)
                CheckState();

            var stopwatch = Stopwatch.StartNew();
            var responseStream = await RequestQueue.SendAsync(request).ConfigureAwait(false);
            stopwatch.Stop();

            double milliseconds = ToMilliseconds(stopwatch);
            await _sentRequestEvent.InvokeAsync(method, endpoint, milliseconds).ConfigureAwait(false);

            return responseStream;
        }

        //Auth
        public async Task ValidateTokenAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            await SendAsync("GET", "auth/login", options: options).ConfigureAwait(false);
        }

        //Channels
        public async Task<Channel> GetChannelAsync(ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<Channel>("GET", $"channels/{channelId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<Channel> GetChannelAsync(ulong guildId, ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                var model = await SendAsync<Channel>("GET", $"channels/{channelId}", options: options).ConfigureAwait(false);
                if (!model.GuildId.IsSpecified || model.GuildId.Value != guildId)
                    return null;
                return model;
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<IReadOnlyCollection<Channel>> GetGuildChannelsAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<Channel>>("GET", $"guilds/{guildId}/channels", options: options).ConfigureAwait(false);
        }
        public async Task<Channel> CreateGuildChannelAsync(ulong guildId, CreateGuildChannelParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.GreaterThan(args.Bitrate, 0, nameof(args.Bitrate));
            Preconditions.NotNullOrWhitespace(args.Name, nameof(args.Name));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Channel>("POST", $"guilds/{guildId}/channels", args, options: options).ConfigureAwait(false);
        }
        public async Task<Channel> DeleteChannelAsync(ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Channel>("DELETE", $"channels/{channelId}", options: options).ConfigureAwait(false);
        }
        public async Task<Channel> ModifyGuildChannelAsync(ulong channelId, ModifyGuildChannelParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Position, 0, nameof(args.Position));
            Preconditions.NotNullOrEmpty(args.Name, nameof(args.Name));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Channel>("PATCH", $"channels/{channelId}", args, options: options).ConfigureAwait(false);
        }
        public async Task<Channel> ModifyGuildChannelAsync(ulong channelId, ModifyTextChannelParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Position, 0, nameof(args.Position));
            Preconditions.NotNullOrEmpty(args.Name, nameof(args.Name));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Channel>("PATCH", $"channels/{channelId}", args, options: options).ConfigureAwait(false);
        }
        public async Task<Channel> ModifyGuildChannelAsync(ulong channelId, ModifyVoiceChannelParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.GreaterThan(args.Bitrate, 0, nameof(args.Bitrate));
            Preconditions.AtLeast(args.UserLimit, 0, nameof(args.Bitrate));
            Preconditions.AtLeast(args.Position, 0, nameof(args.Position));
            Preconditions.NotNullOrEmpty(args.Name, nameof(args.Name));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Channel>("PATCH", $"channels/{channelId}", args, options: options).ConfigureAwait(false);
        }
        public async Task ModifyGuildChannelsAsync(ulong guildId, IEnumerable<ModifyGuildChannelsParams> args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            options = RequestOptions.CreateOrClone(options);

            var channels = args.ToArray();
            switch (channels.Length)
            {
                case 0:
                    return;
                case 1:
                    await ModifyGuildChannelAsync(channels[0].Id, new ModifyGuildChannelParams { Position = channels[0].Position }).ConfigureAwait(false);
                    break;
                default:
                    await SendJsonAsync("PATCH", $"guilds/{guildId}/channels", channels, options: options).ConfigureAwait(false);
                    break;
            }
        }
        
        //Channel Messages
        public async Task<Message> GetChannelMessageAsync(ulong channelId, ulong messageId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(messageId, 0, nameof(messageId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<Message>("GET", $"channels/{channelId}/messages/{messageId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<IReadOnlyCollection<Message>> GetChannelMessagesAsync(ulong channelId, GetChannelMessagesParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Limit, 0, nameof(args.Limit));
            Preconditions.AtMost(args.Limit, DiscordConfig.MaxMessagesPerBatch, nameof(args.Limit));
            options = RequestOptions.CreateOrClone(options);

            int limit = args.Limit.GetValueOrDefault(DiscordConfig.MaxMessagesPerBatch);
            ulong? relativeId = args.RelativeMessageId.IsSpecified ? args.RelativeMessageId.Value : (ulong?)null;
            string relativeDir;

            switch (args.RelativeDirection.GetValueOrDefault(Direction.Before))
            {
                case Direction.Before:
                default:
                    relativeDir = "before";
                    break;
                case Direction.After:
                    relativeDir = "after";
                    break;
                case Direction.Around:
                    relativeDir = "around";
                    break;
            }

            string endpoint;
            if (relativeId != null)
                endpoint = $"channels/{channelId}/messages?limit={limit}&{relativeDir}={relativeId}";
            else
                endpoint = $"channels/{channelId}/messages?limit={limit}";
            return await SendAsync<IReadOnlyCollection<Message>>("GET", endpoint, options: options).ConfigureAwait(false);
        }
        public async Task<Message> CreateMessageAsync(ulong channelId, CreateMessageParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotNullOrEmpty(args.Content, nameof(args.Content));
            if (args.Content.Length > DiscordConfig.MaxMessageSize)
                throw new ArgumentException($"Message content is too long, length must be less or equal to {DiscordConfig.MaxMessageSize}.", nameof(args.Content));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Message>("POST", $"channels/{channelId}/messages", args, options: options).ConfigureAwait(false);
        }
        public async Task<Message> UploadFileAsync(ulong channelId, UploadFileParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            if (args.Content.GetValueOrDefault(null) == null)
                args.Content = "";
            else if (args.Content.IsSpecified)
            {
                if (args.Content.Value == null)
                    args.Content = "";
                if (args.Content.Value?.Length > DiscordConfig.MaxMessageSize)
                    throw new ArgumentOutOfRangeException($"Message content is too long, length must be less or equal to {DiscordConfig.MaxMessageSize}.", nameof(args.Content));
            }

            return await SendMultipartAsync<Message>("POST", $"channels/{channelId}/messages", args.ToDictionary(), options: options).ConfigureAwait(false);
        }
        public async Task DeleteMessageAsync(ulong channelId, ulong messageId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(messageId, 0, nameof(messageId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"channels/{channelId}/messages/{messageId}", options: options).ConfigureAwait(false);
        }
        public async Task DeleteMessagesAsync(ulong channelId, DeleteMessagesParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotNull(args.MessageIds, nameof(args.MessageIds));
            Preconditions.AtMost(args.MessageIds.Length, 100, nameof(args.MessageIds.Length));
            options = RequestOptions.CreateOrClone(options);

            switch (args.MessageIds.Length)
            {
                case 0:
                    return;
                case 1:
                    await DeleteMessageAsync(channelId, args.MessageIds[0]).ConfigureAwait(false);
                    break;
                default:
                    await SendJsonAsync("POST", $"channels/{channelId}/messages/bulk_delete", args, options: options).ConfigureAwait(false);
                    break;
            }
        }
        public async Task<Message> ModifyMessageAsync(ulong channelId, ulong messageId, ModifyMessageParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(messageId, 0, nameof(messageId));
            Preconditions.NotNull(args, nameof(args));
            if (args.Content.IsSpecified)
            {
                Preconditions.NotNullOrEmpty(args.Content, nameof(args.Content));
                if (args.Content.Value.Length > DiscordConfig.MaxMessageSize)
                    throw new ArgumentOutOfRangeException($"Message content is too long, length must be less or equal to {DiscordConfig.MaxMessageSize}.", nameof(args.Content));
            }
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Message>("PATCH", $"channels/{channelId}/messages/{messageId}", args, options: options).ConfigureAwait(false);
        }
        public async Task AckMessageAsync(ulong channelId, ulong messageId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(messageId, 0, nameof(messageId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("POST", $"channels/{channelId}/messages/{messageId}/ack", options: options).ConfigureAwait(false);
        }
        public async Task TriggerTypingIndicatorAsync(ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("POST", $"channels/{channelId}/typing", options: options).ConfigureAwait(false);
        }

        //Channel Permissions
        public async Task ModifyChannelPermissionsAsync(ulong channelId, ulong targetId, ModifyChannelPermissionsParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(targetId, 0, nameof(targetId));
            Preconditions.NotNull(args, nameof(args));
            options = RequestOptions.CreateOrClone(options);

            await SendJsonAsync("PUT", $"channels/{channelId}/permissions/{targetId}", args, options: options).ConfigureAwait(false);
        }
        public async Task DeleteChannelPermissionAsync(ulong channelId, ulong targetId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(targetId, 0, nameof(targetId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"channels/{channelId}/permissions/{targetId}", options: options).ConfigureAwait(false);
        }

        //Channel Pins
        public async Task AddPinAsync(ulong channelId, ulong messageId, RequestOptions options = null)
        {
            Preconditions.GreaterThan(channelId, 0, nameof(channelId));
            Preconditions.GreaterThan(messageId, 0, nameof(messageId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("PUT", $"channels/{channelId}/pins/{messageId}", options: options).ConfigureAwait(false);

        }
        public async Task RemovePinAsync(ulong channelId, ulong messageId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(messageId, 0, nameof(messageId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"channels/{channelId}/pins/{messageId}", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<Message>> GetPinsAsync(ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<Message>>("GET", $"channels/{channelId}/pins", options: options).ConfigureAwait(false);
        }

        //Channel Recipients
        public async Task AddGroupRecipientAsync(ulong channelId, ulong userId, RequestOptions options = null)
        {
            Preconditions.GreaterThan(channelId, 0, nameof(channelId));
            Preconditions.GreaterThan(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("PUT", $"channels/{channelId}/recipients/{userId}", options: options).ConfigureAwait(false);

        }
        public async Task RemoveGroupRecipientAsync(ulong channelId, ulong userId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"channels/{channelId}/recipients/{userId}", options: options).ConfigureAwait(false);
        }

        //Guilds
        public async Task<Guild> GetGuildAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<Guild>("GET", $"guilds/{guildId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<Guild> CreateGuildAsync(CreateGuildParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotNullOrWhitespace(args.Name, nameof(args.Name));
            Preconditions.NotNullOrWhitespace(args.RegionId, nameof(args.RegionId));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Guild>("POST", "guilds", args, options: options).ConfigureAwait(false);
        }
        public async Task<Guild> DeleteGuildAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Guild>("DELETE", $"guilds/{guildId}", options: options).ConfigureAwait(false);
        }
        public async Task<Guild> LeaveGuildAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Guild>("DELETE", $"users/@me/guilds/{guildId}", options: options).ConfigureAwait(false);
        }
        public async Task<Guild> ModifyGuildAsync(ulong guildId, ModifyGuildParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotEqual(args.AfkChannelId, 0, nameof(args.AfkChannelId));
            Preconditions.AtLeast(args.AfkTimeout, 0, nameof(args.AfkTimeout));
            Preconditions.NotNullOrEmpty(args.Name, nameof(args.Name));
            Preconditions.GreaterThan(args.OwnerId, 0, nameof(args.OwnerId));
            Preconditions.NotNull(args.RegionId, nameof(args.RegionId));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Guild>("PATCH", $"guilds/{guildId}", args, options: options).ConfigureAwait(false);
        }
        public async Task<GetGuildPruneCountResponse> BeginGuildPruneAsync(ulong guildId, GuildPruneParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Days, 0, nameof(args.Days));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<GetGuildPruneCountResponse>("POST", $"guilds/{guildId}/prune", args, options: options).ConfigureAwait(false);
        }
        public async Task<GetGuildPruneCountResponse> GetGuildPruneCountAsync(ulong guildId, GuildPruneParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Days, 0, nameof(args.Days));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<GetGuildPruneCountResponse>("GET", $"guilds/{guildId}/prune", args, options: options).ConfigureAwait(false);
        }

        //Guild Bans
        public async Task<IReadOnlyCollection<Ban>> GetGuildBansAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<Ban>>("GET", $"guilds/{guildId}/bans", options: options).ConfigureAwait(false);
        }
        public async Task CreateGuildBanAsync(ulong guildId, ulong userId, CreateGuildBanParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.DeleteMessageDays, 0, nameof(args.DeleteMessageDays));
            options = RequestOptions.CreateOrClone(options);

            await SendJsonAsync("PUT", $"guilds/{guildId}/bans/{userId}", args, options: options).ConfigureAwait(false);
        }
        public async Task RemoveGuildBanAsync(ulong guildId, ulong userId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"guilds/{guildId}/bans/{userId}", options: options).ConfigureAwait(false);
        }

        //Guild Embeds
        public async Task<GuildEmbed> GetGuildEmbedAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<GuildEmbed>("GET", $"guilds/{guildId}/embed", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<GuildEmbed> ModifyGuildEmbedAsync(ulong guildId, ModifyGuildEmbedParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<GuildEmbed>("PATCH", $"guilds/{guildId}/embed", args, options: options).ConfigureAwait(false);
        }

        //Guild Integrations
        public async Task<IReadOnlyCollection<Integration>> GetGuildIntegrationsAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<Integration>>("GET", $"guilds/{guildId}/integrations", options: options).ConfigureAwait(false);
        }
        public async Task<Integration> CreateGuildIntegrationAsync(ulong guildId, CreateGuildIntegrationParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotEqual(args.Id, 0, nameof(args.Id));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Integration>("POST", $"guilds/{guildId}/integrations", options: options).ConfigureAwait(false);
        }
        public async Task<Integration> DeleteGuildIntegrationAsync(ulong guildId, ulong integrationId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(integrationId, 0, nameof(integrationId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Integration>("DELETE", $"guilds/{guildId}/integrations/{integrationId}", options: options).ConfigureAwait(false);
        }
        public async Task<Integration> ModifyGuildIntegrationAsync(ulong guildId, ulong integrationId, ModifyGuildIntegrationParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(integrationId, 0, nameof(integrationId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.ExpireBehavior, 0, nameof(args.ExpireBehavior));
            Preconditions.AtLeast(args.ExpireGracePeriod, 0, nameof(args.ExpireGracePeriod));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Integration>("PATCH", $"guilds/{guildId}/integrations/{integrationId}", args, options: options).ConfigureAwait(false);
        }
        public async Task<Integration> SyncGuildIntegrationAsync(ulong guildId, ulong integrationId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(integrationId, 0, nameof(integrationId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Integration>("POST", $"guilds/{guildId}/integrations/{integrationId}/sync", options: options).ConfigureAwait(false);
        }

        //Guild Invites
        public async Task<Invite> GetInviteAsync(string inviteId, RequestOptions options = null)
        {
            Preconditions.NotNullOrEmpty(inviteId, nameof(inviteId));
            options = RequestOptions.CreateOrClone(options);

            //Remove trailing slash
            if (inviteId[inviteId.Length - 1] == '/')
                inviteId = inviteId.Substring(0, inviteId.Length - 1);
            //Remove leading URL
            int index = inviteId.LastIndexOf('/');
            if (index >= 0)
                inviteId = inviteId.Substring(index + 1);

            try
            {
                return await SendAsync<Invite>("GET", $"invites/{inviteId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<IReadOnlyCollection<InviteMetadata>> GetGuildInvitesAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<InviteMetadata>>("GET", $"guilds/{guildId}/invites", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<InviteMetadata>> GetChannelInvitesAsync(ulong channelId, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<InviteMetadata>>("GET", $"channels/{channelId}/invites", options: options).ConfigureAwait(false);
        }
        public async Task<InviteMetadata> CreateChannelInviteAsync(ulong channelId, CreateChannelInviteParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(channelId, 0, nameof(channelId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.MaxAge, 0, nameof(args.MaxAge));
            Preconditions.AtLeast(args.MaxUses, 0, nameof(args.MaxUses));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<InviteMetadata>("POST", $"channels/{channelId}/invites", args, options: options).ConfigureAwait(false);
        }
        public async Task<Invite> DeleteInviteAsync(string inviteCode, RequestOptions options = null)
        {
            Preconditions.NotNullOrEmpty(inviteCode, nameof(inviteCode));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Invite>("DELETE", $"invites/{inviteCode}", options: options).ConfigureAwait(false);
        }
        public async Task AcceptInviteAsync(string inviteCode, RequestOptions options = null)
        {
            Preconditions.NotNullOrEmpty(inviteCode, nameof(inviteCode));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("POST", $"invites/{inviteCode}", options: options).ConfigureAwait(false);
        }

        //Guild Members
        public async Task<GuildMember> GetGuildMemberAsync(ulong guildId, ulong userId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<GuildMember>("GET", $"guilds/{guildId}/members/{userId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<IReadOnlyCollection<GuildMember>> GetGuildMembersAsync(ulong guildId, GetGuildMembersParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.GreaterThan(args.Limit, 0, nameof(args.Limit));
            Preconditions.AtMost(args.Limit, DiscordConfig.MaxUsersPerBatch, nameof(args.Limit));
            Preconditions.GreaterThan(args.AfterUserId, 0, nameof(args.AfterUserId));
            options = RequestOptions.CreateOrClone(options);

            int limit = args.Limit.GetValueOrDefault(int.MaxValue);
            ulong afterUserId = args.AfterUserId.GetValueOrDefault(0);
            
            string endpoint = $"guilds/{guildId}/members?limit={limit}&after={afterUserId}";
            return await SendAsync<IReadOnlyCollection<GuildMember>>("GET", endpoint, options: options).ConfigureAwait(false);
        }
        public async Task RemoveGuildMemberAsync(ulong guildId, ulong userId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"guilds/{guildId}/members/{userId}", options: options).ConfigureAwait(false);
        }
        public async Task ModifyGuildMemberAsync(ulong guildId, ulong userId, ModifyGuildMemberParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(userId, 0, nameof(userId));
            Preconditions.NotNull(args, nameof(args));
            options = RequestOptions.CreateOrClone(options);

            bool isCurrentUser = userId == CurrentUser.Id;

            if (isCurrentUser && args.Nickname.IsSpecified)
            {
                var nickArgs = new ModifyCurrentUserNickParams(args.Nickname.Value ?? "");
                await ModifyMyNickAsync(guildId, nickArgs).ConfigureAwait(false);
                args.Nickname = Optional.Create<string>(); //Remove
            }
            if (!isCurrentUser || args.Deaf.IsSpecified || args.Mute.IsSpecified || args.RoleIds.IsSpecified)
            {
                await SendJsonAsync("PATCH", $"guilds/{guildId}/members/{userId}", args, options: options).ConfigureAwait(false);
            }
        }

        //Guild Roles
        public async Task<IReadOnlyCollection<Role>> GetGuildRolesAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<Role>>("GET", $"guilds/{guildId}/roles", options: options).ConfigureAwait(false);
        }
        public async Task<Role> CreateGuildRoleAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<Role>("POST", $"guilds/{guildId}/roles", options: options).ConfigureAwait(false);
        }
        public async Task DeleteGuildRoleAsync(ulong guildId, ulong roleId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(roleId, 0, nameof(roleId));
            options = RequestOptions.CreateOrClone(options);

            await SendAsync("DELETE", $"guilds/{guildId}/roles/{roleId}", options: options).ConfigureAwait(false);
        }
        public async Task<Role> ModifyGuildRoleAsync(ulong guildId, ulong roleId, ModifyGuildRoleParams args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotEqual(roleId, 0, nameof(roleId));
            Preconditions.NotNull(args, nameof(args));
            Preconditions.AtLeast(args.Color, 0, nameof(args.Color));
            Preconditions.NotNullOrEmpty(args.Name, nameof(args.Name));
            Preconditions.AtLeast(args.Position, 0, nameof(args.Position));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Role>("PATCH", $"guilds/{guildId}/roles/{roleId}", args, options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<Role>> ModifyGuildRolesAsync(ulong guildId, IEnumerable<ModifyGuildRolesParams> args, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            Preconditions.NotNull(args, nameof(args));
            options = RequestOptions.CreateOrClone(options);

            var roles = args.ToArray();
            switch (roles.Length)
            {
                case 0:
                    return ImmutableArray.Create<Role>();
                case 1:
                    return ImmutableArray.Create(await ModifyGuildRoleAsync(guildId, roles[0].Id, roles[0]).ConfigureAwait(false));
                default:
                    return await SendJsonAsync<IReadOnlyCollection<Role>>("PATCH", $"guilds/{guildId}/roles", args, options: options).ConfigureAwait(false);
            }
        }

        //Users
        public async Task<User> GetUserAsync(ulong userId, RequestOptions options = null)
        {
            Preconditions.NotEqual(userId, 0, nameof(userId));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                return await SendAsync<User>("GET", $"users/{userId}", options: options).ConfigureAwait(false);
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<User> GetUserAsync(string username, string discriminator, RequestOptions options = null)
        {
            Preconditions.NotNullOrEmpty(username, nameof(username));
            Preconditions.NotNullOrEmpty(discriminator, nameof(discriminator));
            options = RequestOptions.CreateOrClone(options);

            try
            {
                var models = await QueryUsersAsync($"{username}#{discriminator}", 1, options: options).ConfigureAwait(false);
                return models.FirstOrDefault();
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public async Task<IReadOnlyCollection<User>> QueryUsersAsync(string query, int limit, RequestOptions options = null)
        {
            Preconditions.NotNullOrEmpty(query, nameof(query));
            Preconditions.AtLeast(limit, 0, nameof(limit));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<User>>("GET", $"users?q={Uri.EscapeDataString(query)}&limit={limit}", options: options).ConfigureAwait(false);
        }

        //Current User/DMs
        public async Task<User> GetMyUserAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<User>("GET", "users/@me", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<Connection>> GetMyConnectionsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<IReadOnlyCollection<Connection>>("GET", "users/@me/connections", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<Channel>> GetMyPrivateChannelsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<IReadOnlyCollection<Channel>>("GET", "users/@me/channels", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<UserGuild>> GetMyGuildsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<IReadOnlyCollection<UserGuild>>("GET", "users/@me/guilds", options: options).ConfigureAwait(false);
        }
        public async Task<Application> GetMyApplicationAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<Application>("GET", "oauth2/applications/@me", options: options).ConfigureAwait(false);
        }
        public async Task<User> ModifySelfAsync(ModifyCurrentUserParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotNullOrEmpty(args.Username, nameof(args.Username));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<User>("PATCH", "users/@me", args, options: options).ConfigureAwait(false);
        }
        public async Task ModifyMyNickAsync(ulong guildId, ModifyCurrentUserNickParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.NotNull(args.Nickname, nameof(args.Nickname));
            options = RequestOptions.CreateOrClone(options);

            await SendJsonAsync("PATCH", $"guilds/{guildId}/members/@me/nick", args, options: options).ConfigureAwait(false);
        }
        public async Task<Channel> CreateDMChannelAsync(CreateDMChannelParams args, RequestOptions options = null)
        {
            Preconditions.NotNull(args, nameof(args));
            Preconditions.GreaterThan(args.RecipientId, 0, nameof(args.RecipientId));
            options = RequestOptions.CreateOrClone(options);

            return await SendJsonAsync<Channel>("POST", $"users/@me/channels", args, options: options).ConfigureAwait(false);
        }

        //Voice Regions
        public async Task<IReadOnlyCollection<VoiceRegion>> GetVoiceRegionsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendAsync<IReadOnlyCollection<VoiceRegion>>("GET", "voice/regions", options: options).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<VoiceRegion>> GetGuildVoiceRegionsAsync(ulong guildId, RequestOptions options = null)
        {
            Preconditions.NotEqual(guildId, 0, nameof(guildId));
            options = RequestOptions.CreateOrClone(options);

            return await SendAsync<IReadOnlyCollection<VoiceRegion>>("GET", $"guilds/{guildId}/regions", options: options).ConfigureAwait(false);
        }

        //Helpers
        protected void CheckState()
        {
            if (LoginState != LoginState.LoggedIn)
                throw new InvalidOperationException("Client is not logged in.");
        }
        protected static double ToMilliseconds(Stopwatch stopwatch) => Math.Round((double)stopwatch.ElapsedTicks / (double)Stopwatch.Frequency * 1000.0, 2);
        protected string SerializeJson(object value)
        {
            var sb = new StringBuilder(256);
            using (TextWriter text = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (JsonWriter writer = new JsonTextWriter(text))
                _serializer.Serialize(writer, value);
            return sb.ToString();
        }
        protected T DeserializeJson<T>(Stream jsonStream)
        {
            using (TextReader text = new StreamReader(jsonStream))
            using (JsonReader reader = new JsonTextReader(text))
                return _serializer.Deserialize<T>(reader);
        }
    }
}
