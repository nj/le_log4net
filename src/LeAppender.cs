// 
// Copyright (c) 2010-2012 Logentries, Jlizard
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Logentries nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 
// Mark Lacomber <marklacomber@gmail.com>
// Viliam Holub <vilda@logentries.com>


﻿using System;
using System.Collections.Concurrent;
using System.Configuration;
﻿using System.Linq;
﻿using System.Text.RegularExpressions;
using System.Text;
﻿using System.Net.Security;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using log4net.Core;
using log4net.Util;

namespace log4net.Appender
{
    public class LeAppender : AppenderSkeleton
    {
        /*
         * Constants
         */

        /** Size of the internal event queue. */
        public static readonly int QUEUE_SIZE = 32768;
        /** Logentries API server address. */
        static readonly String LE_API = "api.logentries.com";
        /** Port number for token logging on Logentries API server. */
        static readonly int LE_PORT = 10000;
        /** UTF-8 output character set. */
        static readonly UTF8Encoding UTF8 = new UTF8Encoding();
        /** ASCII character set used by HTTP. */
        static readonly ASCIIEncoding ASCII = new ASCIIEncoding();
        /** Minimal delay between attempts to reconnect in milliseconds. */
        static readonly int MIN_DELAY = 100;
        /** Maximal delay between attempts to reconnect in milliseconds. */
        static readonly int MAX_DELAY = 10000;
        /** LE appender signature - used for debugging messages. */
        static readonly String LE = "LE: ";
        /** Logentries Config Key */
        static readonly String CONFIG_TOKEN = "LOGENTRIES_TOKEN";
        /** Error message displayed when invalid token is detected. */
        static readonly String INVALID_TOKEN = "\n\nIt appears your LOGENTRIES_TOKEN parameter in web/app.config is invalid!\n\n";

        readonly Random random = new Random();

        private TcpClient client = null;
        private Stream sock = null;
        public Thread thread;
        public bool started = false;
        private String token = null;
        private char[] trimChars = { '\n' };
        /** Message Queue */
        public BlockingCollection<Byte[]> queue;

        /** Logentries Parameters */
        private bool m_Debug;

        #region Public Instance Properties

        public bool Debug
        {
            get { return m_Debug; }
            set { m_Debug = value; }
        }

        #endregion

        #region Constructor

        public LeAppender()
        {
            queue = new BlockingCollection<byte[]>(QUEUE_SIZE);

            thread = new Thread(new ThreadStart(run_loop));
            thread.Name = "Logentries Log4net Appender";
            thread.IsBackground = true;
        }

        #endregion

        private void openConnection()
        {
            try
            {
                this.client = new TcpClient(LE_API, LE_PORT);
                this.client.NoDelay = true;

                this.sock = this.client.GetStream();

                this.token = this.SubstituteAppSetting(CONFIG_TOKEN);
            }
            catch
            {
                throw new IOException();
            }
        }

        private void reopenConnection()
        {
            closeConnection();

            int root_delay = MIN_DELAY;
            while (true)
            {
                try
                {
                    openConnection();

                    return;
                }
                catch (Exception e)
                {
                    if (Debug)
                    {
                        WriteDebugMessages("Unable to connect to Logentries");
                    }
                }

                root_delay *= 2;
                if (root_delay > MAX_DELAY)
                    root_delay = MAX_DELAY;
                int wait_for = root_delay + random.Next(root_delay);

                try
                {
                    Thread.Sleep(wait_for);
                }
                catch
                {
                    throw new ThreadInterruptedException();
                }
            }
        }

        private void closeConnection()
        {
            if (this.client != null)
                this.client.Close();
        }

        public void run_loop()
        {
            try
            {
                //Open connection
                reopenConnection();

                //Send data in queue
                while (true)
                {
                    //Take data from queue
                    byte[] data = queue.Take();

                    //Send data, reconnect if needed
                    while (true)
                    {
                        try
                        {
                            this.sock.Write(data, 0, data.Length);
                            this.sock.Flush();
                        }
                        catch (IOException e)
                        {
                            //Reopen the lost connection
                            reopenConnection();
                            continue;
                        }
                        break;
                    }
                }
            }
            catch (ThreadInterruptedException e)
            {
                WriteDebugMessages("Logentries asynchronous socket client interrupted");
            }
        }

        public void addLine(String line)
        {
            WriteDebugMessages("Queueing " + line);

            byte[] data = UTF8.GetBytes(this.token + line+'\n');

            //Try to append data to queue
            bool is_full = !queue.TryAdd(data);

            //If its full, remove latest item and try again
            if (is_full)
            {
                queue.Take();
                queue.TryAdd(data);
            }
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            if (!checkCredentials())
            {
                WriteDebugMessages(INVALID_TOKEN);
                return;
            }
            if (!started)
            {
                WriteDebugMessages("Starting Logentries asynchronous socket client");
                thread.Start();
                started = true;
            }

            //Render message content
            String renderedEvent = RenderLoggingEvent(loggingEvent);

            renderedEvent = renderedEvent.TrimEnd(trimChars);

            //Replace newline with line separator to maintain formatting
            renderedEvent = renderedEvent.Replace('\n', '\u2028');

            addLine(renderedEvent);

        }

        protected override void Append(LoggingEvent[] loggingEvents)
        {
            foreach (LoggingEvent logEvent in loggingEvents)
            {
                this.Append(logEvent);
            }
        }

        protected override bool RequiresLayout
        {
            get { return true; }
        }

        protected override void OnClose()
        {
            thread.Interrupt();
        }

        public bool checkCredentials()
        {
            var appSettings = ConfigurationManager.AppSettings;
            if (!appSettings.AllKeys.Contains(CONFIG_TOKEN))
                return false;
            if (appSettings[CONFIG_TOKEN] == "")
                return false;
            System.Guid newGuid = System.Guid.NewGuid();
            if (!System.Guid.TryParse(appSettings[CONFIG_TOKEN], out newGuid))
            {
                WriteDebugMessages(INVALID_TOKEN);
                return false;
            }
            return true;
        }
		
		//Used for UnitTests, Append method is protected
		public void TestAppend(LoggingEvent logEvent)
		{
			this.Append(logEvent);
		}

        private void WriteDebugMessages(string message, Exception e)
        {
            message = LE + message;
            if (!Debug) 
               return;
            string[] messages = {message, e.ToString()};
            foreach (var msg in messages)
            {
                LogLog.Debug(typeof(LeAppender), msg);
            }
        }

        private void WriteDebugMessages(string message)
        {
            message = LE + message;
            if (!Debug) 
               return;

	        LogLog.Debug(typeof(LeAppender), message);
        }

        private string SubstituteAppSetting(string key)
        {
            var appSettings = ConfigurationManager.AppSettings;
            if (appSettings.HasKeys() && appSettings.AllKeys.Contains(key))
            {
                return appSettings[key];
            }
            else
            {
                return key;
            }
        }
    }
}
