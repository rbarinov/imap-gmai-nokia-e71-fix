﻿using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Net.Security;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace testimap
{
	class MainClass
	{
		public static void Main (string[] args)
		{
			string host, slport, sport, pfx;

			if (args.Length < 3) {
				throw new ArgumentException ("use args: proxyport host port");
			}

			slport = args [0];
			host = args [1];
			sport = args [2];

			pfx = null;
			X509Certificate serverCert = null;
			var log = NLog.LogManager.GetCurrentClassLogger ();

			if (args.Length > 3) {
				pfx = args [3];
				serverCert = new X509Certificate2 (pfx);

				log.Warn ("loaded certificate for subj : " + serverCert.Subject);
			} else {
				log.Warn ("starting in insecure mode");
			}

			int localport = int.Parse (slport);
			int port = int.Parse (sport);

			TcpListener listener = new TcpListener (localport);

			listener.Start ();


			log.Info ("Listener started on " + localport);

			Task.Run (async () => {

				while (true) {
					var incoming = await listener.AcceptTcpClientAsync ();

					var client = new TcpClient ();
					await client.ConnectAsync (host, port);

					log.Warn ("Got connection");

					var rawStream = client.GetStream ();
					Stream inputStream = incoming.GetStream ();

					SslStream enc = null;

					try {
						if (pfx != null) {
							var ss = new SslStream (inputStream, false);
							await ss.AuthenticateAsServerAsync (serverCert);
							inputStream = ss;
							log.Warn("Running secure SSL stream from client");
						} else {
							log.Fatal("Running insecure stream from client");
						}

						var ssl = new SslStream (rawStream);

						await ssl.AuthenticateAsClientAsync (host, new X509CertificateCollection (), System.Security.Authentication.SslProtocols.Tls12,
							false
						);

						var check = ssl.IsAuthenticated;

						enc = ssl;
					} catch (Exception e) {
						incoming.Close();
						client.Close();
						return;
					}

					Task.Run (async () => {
						while (true) {
							await Task.Delay (50);	
							if (!IsConnected (client.Client) || !IsConnected (incoming.Client)) {
								log.Warn ("Connection closed from one of the parties");
								incoming.Close ();
								client.Close ();
							}
						}
					});

					var clientStream = enc;
					Task.Run (async () => {
						var buffer = new byte[1024];
						while (true) {
							try {
								var read = await inputStream.ReadAsync (buffer, 0, 1024);
								if (read > 0) {
									log.Debug ("IN " + Encoding.UTF8.GetString (buffer, 0, read));

									await clientStream.WriteAsync (buffer, 0, read);
									await clientStream.FlushAsync ();
								} else {
									await Task.Delay (50);
									if (!IsConnected (client.Client) || !IsConnected (incoming.Client)) {
										throw new Exception ();
									}
								}
							} catch (Exception e) {
								incoming.Close ();
								client.Close ();

								log.Warn ("Connection closed on IN read " + e.GetType ().Name + " " + e.Message);
								return;
							}
						}
					});

					Task.Run (async () => {
						var buffer = new byte[1024];
						while (true) {
							try {
								var read = await clientStream.ReadAsync (buffer, 0, 1024);

								if (read > 0) {
									
									log.Debug ("OUT " + Encoding.UTF8.GetString (buffer, 0, read));

									await inputStream.WriteAsync (buffer, 0, read);
									await inputStream.FlushAsync ();
								} else {
									await Task.Delay (50);
									if (!IsConnected (client.Client) || !IsConnected (incoming.Client)) {
										throw new Exception ();
									}
								}
							} catch (Exception e) {
								incoming.Close ();
								client.Close ();

								log.Warn ("Connection closed on OUT read " + e.GetType ().Name + " " + e.Message);
								return;
							}
						}
					});
				}

			}).Wait ();
		}

		public static bool IsConnected(Socket socket)
		{
			try
			{
				return !(socket.Poll(1, SelectMode.SelectRead & SelectMode.SelectWrite) && socket.Available == 0);
			}
			catch (SocketException) { return false; }
		}
	}
}
