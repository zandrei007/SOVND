﻿/*-
 * Copyright (c) 2014 Software Development Solutions, Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
 * OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
 * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
 * OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Threading;

using SpotifyClient;

using libspotifydotnet;
using NAudio.Wave;

namespace SpotifyClient
{
    public class SpotifyTrackDataPipe : IWaveProvider
    {
        private IntPtr _trackPtr;
        private Action<byte[]> d_OnAudioDataArrived = null;
        private Action<object> d_OnAudioStreamComplete = null;
        private Queue<byte[]> _q = new Queue<byte[]>();
        private bool _complete;

        private int buffoffset = 0;

        private static bool _interrupt;
        private static object _syncObj = new object();

        public WaveFormat WaveFormat
        {
            get { return new WaveFormat(); }
        }

        public SpotifyTrackDataPipe(IntPtr trackPtr)
        {
            _trackPtr = trackPtr;

            d_OnAudioDataArrived = new Action<byte[]>(Session_OnAudioDataArrived);
            d_OnAudioStreamComplete = new Action<object>(Session_OnAudioStreamComplete);
        }

        public bool Complete { get { return _complete; } }

        public void StartStreaming()
        {
            _interrupt = true;

            lock (_syncObj)
            {
                _interrupt = false;

                Session.OnAudioDataArrived += d_OnAudioDataArrived;
                Session.OnAudioStreamComplete += d_OnAudioStreamComplete;

                var error = Session.LoadPlayer(_trackPtr);

                if (error != libspotify.sp_error.OK)
                {
                    throw new Exception("[Spotify] \{libspotify.sp_error_message(error)}");
                }

                libspotify.sp_availability avail = libspotify.sp_track_get_availability(Session.SessionPtr, _trackPtr);

                if (avail != libspotify.sp_availability.SP_TRACK_AVAILABILITY_AVAILABLE)
                    throw new Exception("Track is unavailable (\{avail}).");

                Session.Play();

                byte[] buffer = null;

                while (!_interrupt && !_complete || _q.Count > 0)
                {
                    Thread.Sleep(50);
                }

                Session.OnAudioDataArrived -= d_OnAudioDataArrived;
                Session.OnAudioStreamComplete -= d_OnAudioStreamComplete;

                Session.UnloadPlayer();
            }
        }

        private void Session_OnAudioStreamComplete(object obj)
        {
            _complete = true;
        }

        private void Session_OnAudioDataArrived(byte[] buffer)
        {
            if (!_interrupt
                && !_complete)
            {
                _q.Enqueue(buffer);
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_q.Count == 0)
                return 0;

            var buf = _q.Peek();
            Array.Copy(buf, buffoffset, buffer, offset, count);
            buffoffset += count;
            if (buffoffset >= buf.Length)
            {
                buffoffset = 0;
                _q.Dequeue();
            }
            return count;
        }
    }
}