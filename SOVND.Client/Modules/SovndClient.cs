using System.Collections.Generic;
using Anotar.NLog;
using Newtonsoft.Json;
using SOVND.Client.Util;
using SOVND.Lib.Handlers;
using SOVND.Lib.Models;
using SpotifyClient;

namespace SOVND.Client.Modules
{
    public class SovndClient : SOVNDModule
    {
        private readonly IChannelHandlerFactory _chf;

        private readonly string Username;

        public ChannelHandler SubscribedChannelHandler;

        private Dictionary<string, int> votes = new Dictionary<string, int>();
        private Dictionary<string, bool> uservotes = new Dictionary<string, bool>();

        public SovndClient(AuthPair auth, IChannelHandlerFactory chf, ChannelDirectory channels) : base(auth)
        {
            _chf = chf;

            Username = auth.Settings.GetAuthSettings().SOVNDUsername;
            Logging.SetupLogging(Username);

            // TODO Track channel list
            // TODO Track playlist for channel

            // On /channel/info -> track channel list
            // On /selectedchannel/ nowplaying,playlist,stats,chat -> track playlist, subscribed channel details

            // TODO: Need to move all of this to somewhere channel specific
            
            // TODO: We don't need to be subbed to this all the time, just when browsing for channels
            On["/{channel}/info"] = _ =>
            {
                Channel channel = JsonConvert.DeserializeObject<Channel>(_.Message);

                channels.AddChannel(channel);
            };

            SubscribedChannelHandler = chf.CreateChannelHandler("ambient");
        }

        internal void SendChat(string text)
        {
            if (SubscribedChannelHandler != null)
                Publish("/user/\{Username}/\{SubscribedChannelHandler.MQTTName}/chat", text);
            else
                LogTo.Warn("Cannot send chat: not subscribed to a channel");
        }

        public bool RegisterChannel(string name, string description, string image)
        {
            var channel = new Channel
            {
                Name = name,
                Description = description
            };
            return RegisterChannel(channel);
        }

        public bool RegisterChannel(Channel channel)
        {
            // TODO: Detect success or figure out a way to come close (eg check channels that have been registered locally)

            if (channel == null || string.IsNullOrWhiteSpace(channel.Name))
                return false;

            var msg = JsonConvert.SerializeObject(channel);

            Publish("/user/\{Username}/register/\{channel.Name}", msg);
            return true;
        }

        internal void SubscribeToChannel(string channel)
        {
            this.SubscribedChannelHandler = _chf.CreateChannelHandler(channel);
        }

        public void AddTrack(Track track)
        {
            if (SubscribedChannelHandler != null && SubscribedChannelHandler.MQTTName != null)
            {
                Publish("/user/\{Username}/\{SubscribedChannelHandler.MQTTName}/songs/\{track.SongID}", "vote");
                Publish("/user/\{Username}/\{SubscribedChannelHandler.MQTTName}/songssearch/", track.Name + " " + track?.Artists[0]);
            }
            else
                LogTo.Warn("Not subscribed to a channel or channel subscription is malformed (null or MQTTName null)");
        }
    }
}