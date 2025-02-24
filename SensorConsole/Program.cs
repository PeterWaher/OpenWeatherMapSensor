using OpenWeatherMapApi;
using System.Reflection;
using Waher.Content.Html.Elements;
using Waher.Content.Markdown;
using Waher.Content;
using Waher.Content.QR;
using Waher.Content.Xml;
using Waher.Events;
using Waher.Events.Console;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.BitsOfBinary;
using Waher.Networking.XMPP.Chat;
using Waher.Networking.XMPP.Control;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence.Files;
using Waher.Persistence.Serialization;
using Waher.Runtime.Inventory;
using Waher.Runtime.Settings;
using Waher.Script.Graphs;
using Waher.Things.SensorData;
using Waher.Script;
using System.Text;
using Waher.Persistence;
using System.Security.Cryptography;
using Waher.Content.QR.Encoding;
using Waher.Script.Units;
using Waher.Security;
using Waher.Things.ControlParameters;
using Waher.Things;
using SensorConsole.Data;
using Waher.Persistence.Filters;
using Waher.Networking.XMPP.ServiceDiscovery;
using System.Web;

namespace SensorConsole // Note: actual namespace depends on the project name.
{
	internal class Program
	{
		private const int recordsInMemory = 250;    // Number of records to keep in persisted memory, for each time scale.
		private const int fieldBatchSize = 50;      // Number of fields to report in each message.

		private static FilesProvider? db = null;
		private static Timer? sampleTimer = null;
		private static XmppClient? xmppClient = null;
		//private PepClient pepClient = null;

		private static readonly QrEncoder? qrEncoder = new();
		private static string? deviceId;
		private static string? thingRegistryJid = string.Empty;
		private static string? provisioningJid = string.Empty;
		private static string? ownerJid = string.Empty;
		private static OpenWeatherMapClient? weatherClient = null;
		private static SensorServer? sensorServer = null;
		private static ControlServer? controlServer = null;
		private static ThingRegistryClient? registryClient = null;
		private static ProvisioningClient? provisioningClient = null;
		private static BobClient? bobClient = null;
		private static ChatServer? chatServer = null;
		private static DateTime lastPublished = DateTime.MinValue;

		static async Task Main(string[] _)
		{
			try
			{
				Console.ForegroundColor = ConsoleColor.White;

				#region Initializing system

				Log.Register(new ConsoleEventSink(false, false));
				Log.Informational("Starting application.");

				Types.Initialize(
					typeof(Database).GetTypeInfo().Assembly,
					typeof(FilesProvider).GetTypeInfo().Assembly,
					typeof(ObjectSerializer).GetTypeInfo().Assembly,    // Waher.Persistence.Serialization was broken out of Waher.Persistence.FilesLW after the publishing of the MIoT book.
					typeof(RuntimeSettings).GetTypeInfo().Assembly,
					typeof(IContentEncoder).GetTypeInfo().Assembly,
					typeof(XmppClient).GetTypeInfo().Assembly,
					typeof(MarkdownDocument).GetTypeInfo().Assembly,
					typeof(XML).GetTypeInfo().Assembly,
					typeof(Expression).GetTypeInfo().Assembly,
					typeof(Graph).GetTypeInfo().Assembly,
					typeof(Select).GetTypeInfo().Assembly,
					typeof(Program).GetTypeInfo().Assembly);

				#endregion

				#region Setting up database

				db = await FilesProvider.CreateAsync(Path.Combine(Environment.CurrentDirectory, "Data"),
					"Default", 8192, 1000, 8192, Encoding.UTF8, 10000);
				await db.RepairIfInproperShutdown(null);
				
				Database.Register(db);

				await Types.StartAllModules(60000);

				#region Device ID

				deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(deviceId))
				{
					deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", deviceId);
				}

				Log.Informational("Device ID: " + deviceId);

				#endregion

				#endregion

				#region Open Weather Map settings

				string ApiKey = await RuntimeSettings.GetAsync("OpenWeatherMap.ApiKey", string.Empty);
				string Location = await RuntimeSettings.GetAsync("OpenWeatherMap.Location", string.Empty);
				string Country = await RuntimeSettings.GetAsync("OpenWeatherMap.Country", string.Empty);
				bool Updated = false;

				while (true)
				{
					if (!string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(Location) && !string.IsNullOrEmpty(Country))
					{
						try
						{
							Log.Informational("Testing Open Weather Map API connection parameters.");

							weatherClient = new OpenWeatherMapClient(ApiKey, Location, Country);
							await weatherClient.GetData(); // Test parameters

							if (Updated)
							{
								await RuntimeSettings.SetAsync("OpenWeatherMap.ApiKey", ApiKey);
								await RuntimeSettings.SetAsync("OpenWeatherMap.Location", Location);
								await RuntimeSettings.SetAsync("OpenWeatherMap.Country", Country);
							}

							Log.Informational("Open Weather Map API Connection parameters OK.");
							break;
						}
						catch (Exception ex)
						{
							Log.Error("Unable to connect to Open Weather Map API. The following error was reported:\r\n\r\n" + ex.Message);
						}
					}

					ApiKey = InputString("Open Weather Map API Key", ApiKey);
					Location = InputString("Location", Location);
					Country = InputString("Country Code", Country);
					Updated = true;
				}

				#endregion

				#region XMPP Connection

				string Host = await RuntimeSettings.GetAsync("XmppHost", "waher.se");
				int Port = (int)await RuntimeSettings.GetAsync("XmppPort", 5222);
				string UserName = await RuntimeSettings.GetAsync("XmppUserName", string.Empty);
				string PasswordHash = await RuntimeSettings.GetAsync("XmppPasswordHash", string.Empty);
				string PasswordHashMethod = await RuntimeSettings.GetAsync("XmppPasswordHashMethod", string.Empty);

				Updated = false;

				while (true)
				{
					if (!string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(PasswordHash))
					{
						try
						{
							SafeDispose(ref xmppClient);

							if (string.IsNullOrEmpty(PasswordHashMethod))
								xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, "en", typeof(Program).GetTypeInfo().Assembly);
							else
								xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en", typeof(Program).GetTypeInfo().Assembly);

							xmppClient.AllowCramMD5 = false;
							xmppClient.AllowDigestMD5 = false;
							xmppClient.AllowPlain = false;
							xmppClient.AllowScramSHA1 = true;
							xmppClient.AllowScramSHA256 = true;

							xmppClient.AllowRegistration();                /* Allows registration on servers that do not require signatures. */
							// xmppClient.AllowRegistration(Key, Secret);	/* Allows registration on servers requiring a signature of the registration request. */

							xmppClient.OnStateChanged += (sender, State) =>
							{
								Log.Informational("Changing state: " + State.ToString());

								switch (State)
								{
									case XmppState.Connected:
										Log.Informational("Connected as " + xmppClient.FullJID);
										break;
								}

								return Task.CompletedTask;
							};

							xmppClient.OnConnectionError += (sender, ex) =>
							{
								Log.Error(ex.Message);
								return Task.CompletedTask;
							};

							Log.Informational("Connecting to " + xmppClient.Host + ":" + xmppClient.Port.ToString());
							await xmppClient.Connect();

							switch (await xmppClient.WaitStateAsync(10000, XmppState.Connected, XmppState.Error, XmppState.Offline))
							{
								case 0: // Connected
									break;

								case 1: // Error
									throw new Exception("An error occurred when trying to connect. Please revise parameters and try again.");

								case 2: // Offline
								default:
									throw new Exception("Unable to connect to host. Please revise parameters and try again.");
							}

							if (Updated)
							{
								await RuntimeSettings.SetAsync("XmppHost", Host = xmppClient.Host);
								await RuntimeSettings.SetAsync("XmppPort", Port = xmppClient.Port);
								await RuntimeSettings.SetAsync("XmppUserName", UserName = xmppClient.UserName);
								await RuntimeSettings.SetAsync("XmppPasswordHash", PasswordHash = xmppClient.PasswordHash);
								await RuntimeSettings.SetAsync("XmppPasswordHashMethod", PasswordHashMethod = xmppClient.PasswordHashMethod);
							}

							break;
						}
						catch (Exception ex)
						{
							Log.Error("Unable to connect to XMPP Network. The following error was reported:\r\n\r\n" + ex.Message);
						}
					}

					Host = InputString("XMPP Broker", Host);
					Port = InputString("Port", Port);
					UserName = InputString("User Name", UserName);
					PasswordHash = InputString("Password", PasswordHash);
					PasswordHashMethod = string.Empty;
					Updated = true;
				}

				#endregion

				#region Basic XMPP events & features

				xmppClient.OnError += (Sender, ex) =>
				{
					Log.Error(ex);
					return Task.CompletedTask;
				};

				xmppClient.OnPasswordChanged += (Sender, e) =>
				{
					Log.Informational("Password changed.", xmppClient.BareJID);
					return Task.CompletedTask;
				};

				xmppClient.OnPresenceSubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship request accepted.", xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				xmppClient.OnPresenceUnsubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship removal accepted.", xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				RegisterVCard();

				#endregion

				#region Configuring Decision Support & Provisioning

				thingRegistryJid = await RuntimeSettings.GetAsync("ThingRegistry.JID", string.Empty);
				provisioningJid = await RuntimeSettings.GetAsync("ProvisioningServer.JID", thingRegistryJid);
				ownerJid = await RuntimeSettings.GetAsync("ThingRegistry.Owner", string.Empty);

				if (string.IsNullOrEmpty(thingRegistryJid) || string.IsNullOrEmpty(provisioningJid))
				{
					Log.Informational("Searching for Thing Registry and Provisioning Server.");

					ServiceItemsDiscoveryEventArgs e = await xmppClient.ServiceItemsDiscoveryAsync(xmppClient.Domain);
					foreach (Item Item in e.Items)
					{
						ServiceDiscoveryEventArgs e2 = await xmppClient.ServiceDiscoveryAsync(Item.JID);

						try
						{
							if (e2.HasAnyFeature(ProvisioningClient.NamespacesProvisioningDevice))
							{
								await RuntimeSettings.SetAsync("ProvisioningServer.JID", provisioningJid = Item.JID);
								Log.Informational("Provisioning server found.", provisioningJid);
							}

							if (e2.HasAnyFeature(ThingRegistryClient.NamespacesDiscovery))
							{
								await RuntimeSettings.SetAsync("ThingRegistry.JID", thingRegistryJid = Item.JID);
								Log.Informational("Thing registry found.", thingRegistryJid);
							}
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
					}
				}

				if (!string.IsNullOrEmpty(provisioningJid))
				{
					provisioningClient = new ProvisioningClient(xmppClient, provisioningJid, ownerJid);

					provisioningClient.CacheCleared += (sender, e) =>
					{
						Log.Informational("Rule cache cleared.");
						return Task.CompletedTask;
					};
				}

				if (!string.IsNullOrEmpty(thingRegistryJid))
				{
					registryClient = new ThingRegistryClient(xmppClient, thingRegistryJid);

					registryClient.Claimed += async (sender, e) =>
					{
						try
						{
							Log.Notice("Owner claimed device.", string.Empty, e.JID);

							await RuntimeSettings.SetAsync("ThingRegistry.Owner", ownerJid = e.JID);
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);

							Reregister();
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
					};

					registryClient.Disowned += async (sender, e) =>
					{
						try
						{
							Log.Notice("Owner disowned device.", string.Empty, ownerJid);

							await RuntimeSettings.SetAsync("ThingRegistry.Owner", ownerJid = string.Empty);
							
							Reregister();
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
					};
				}

				await RegisterDevice();

				#endregion

				SetupSensorServer();
				SetupControlServer();
				SetupChatServer();

				await SetupSampleTimer();

				bool Running = true;

				Console.CancelKeyPress += (_, e) =>
				{
					Running = false;
					e.Cancel = true;
				};

				while (Running)
					await Task.Delay(1000);
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.Out.WriteLine(ex.Message);
			}
			finally
			{
				SafeDispose(ref sampleTimer);
				//SafeDispose(ref pepClient);
				SafeDispose(ref chatServer);
				SafeDispose(ref bobClient);
				SafeDispose(ref sensorServer);
				SafeDispose(ref controlServer);
				SafeDispose(ref xmppClient);

				await Log.TerminateAsync();

				await Types.StopAllModules();
			}
		}

		private static int InputString(string Prompt, int DefaultValue)
		{
			while (true)
			{
				string s = InputString(Prompt, DefaultValue.ToString());
				if (int.TryParse(s, out int Result))
					return Result;

				Log.Error("Input must be an integer.");
			}
		}

		private static string InputString(string Prompt, string DefaultValue)
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.Out.Write(Prompt);
			Console.Out.WriteLine(":");

			if (!string.IsNullOrEmpty(DefaultValue))
			{
				Console.Write("(Accept default value by pressing ENTER: ");
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.Write(DefaultValue);
				Console.ForegroundColor = ConsoleColor.White;
				Console.WriteLine(")");
			}

			Console.Write("> ");
			Console.ForegroundColor = ConsoleColor.Yellow;

			string? s = Console.In.ReadLine();
			if (string.IsNullOrEmpty(s))
				s = DefaultValue;

			Console.ForegroundColor = ConsoleColor.White;

			return s;
		}

		private static void SafeDispose<T>(ref T? Object)
			where T : IDisposable
		{
			try
			{
				Object?.Dispose();
				Object = default;
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}

		#region vCard contact information for device

		// XEP-0054 - vcard-temp: http://xmpp.org/extensions/xep-0054.html

		private static void RegisterVCard()
		{
			xmppClient?.RegisterIqGetHandler("vCard", "vcard-temp", (sender, e) =>
			{
				e.IqResult(GetVCardXml());
				return Task.CompletedTask;
			}, true);

			Log.Informational("Setting vCard");
			xmppClient?.SendIqSet(string.Empty, GetVCardXml(), (sender, e) =>
			{
				if (e.Ok)
					Log.Informational("vCard successfully set.");
				else
					Log.Error("Unable to set vCard.");

				return Task.CompletedTask;

			}, null);
		}

		private static string GetVCardXml()
		{
			StringBuilder Xml = new();

			Xml.Append("<vCard xmlns='vcard-temp'>");
			Xml.Append("<FN>Open Weather Map Sensor</FN><N><FAMILY>Sensor</FAMILY><GIVEN>Open Weather Map</GIVEN><MIDDLE/></N>");
			Xml.Append("<URL>https://github.com/PeterWaher/OpenWeatherMapSensor</URL>");
			Xml.Append("<JABBERID>");
			Xml.Append(XML.Encode(xmppClient?.BareJID));
			Xml.Append("</JABBERID>");
			Xml.Append("<UID>");
			Xml.Append(deviceId);
			Xml.Append("</UID>");
			Xml.Append("<DESC>XMPP Sensor Project (OpenWeatherMapSensor), based on a sensor example from the book Mastering Internet of Things, by Peter Waher.</DESC>");

			// XEP-0153 - vCard-Based Avatars: http://xmpp.org/extensions/xep-0153.html

			using Stream? fs = typeof(Program).Assembly.GetManifestResourceStream("SensorConsole.Assets.Icon.png");

			if (fs is not null)
			{
				int Len = (int)fs.Length;
				byte[] Icon = new byte[Len];
				fs.Read(Icon, 0, Len);

				Xml.Append("<PHOTO><TYPE>image/png</TYPE><BINVAL>");
				Xml.Append(Convert.ToBase64String(Icon));
				Xml.Append("</BINVAL></PHOTO>");
			}

			Xml.Append("</vCard>");

			return Xml.ToString();
		}

		#endregion

		#region Device Registration

		private static async Task RegisterDevice()
		{
			string Country = await RuntimeSettings.GetAsync("ThingRegistry.Country", string.Empty);
			string Region = await RuntimeSettings.GetAsync("ThingRegistry.Region", string.Empty);
			string City = await RuntimeSettings.GetAsync("ThingRegistry.City", string.Empty);
			string Area = await RuntimeSettings.GetAsync("ThingRegistry.Area", string.Empty);
			string Street = await RuntimeSettings.GetAsync("ThingRegistry.Street", string.Empty);
			string StreetNr = await RuntimeSettings.GetAsync("ThingRegistry.StreetNr", string.Empty);
			string Building = await RuntimeSettings.GetAsync("ThingRegistry.Building", string.Empty);
			string Apartment = await RuntimeSettings.GetAsync("ThingRegistry.Apartment", string.Empty);
			string Room = await RuntimeSettings.GetAsync("ThingRegistry.Room", string.Empty);
			string Name = await RuntimeSettings.GetAsync("ThingRegistry.Name", string.Empty);
			bool HasLocation = await RuntimeSettings.GetAsync("ThingRegistry.Location", false);
			bool Updated = false;

			while (true)
			{
				if (HasLocation)
				{
					try
					{
						List<MetaDataTag> MetaInfo = new()
						{
							new MetaDataStringTag("CLASS", "Sensor"),
							new MetaDataStringTag("TYPE", "Open Weather Map"),
							new MetaDataStringTag("MAN", "waher.se"),
							new MetaDataStringTag("MODEL", "OpenWeatherMapSensor"),
							new MetaDataStringTag("PURL", "https://github.com/PeterWaher/OpenWeatherMapSensor"),
							new MetaDataStringTag("SN", deviceId),
							new MetaDataNumericTag("V", 2.0)
						};

						if (!string.IsNullOrEmpty(Country))
							MetaInfo.Add(new MetaDataStringTag("COUNTRY", Country));

						if (!string.IsNullOrEmpty(Region))
							MetaInfo.Add(new MetaDataStringTag("REGION", Region));

						if (!string.IsNullOrEmpty(City))
							MetaInfo.Add(new MetaDataStringTag("CITY", City));

						if (!string.IsNullOrEmpty(Area))
							MetaInfo.Add(new MetaDataStringTag("AREA", Area));

						if (!string.IsNullOrEmpty(Street))
							MetaInfo.Add(new MetaDataStringTag("STREET", Street));

						if (!string.IsNullOrEmpty(StreetNr))
							MetaInfo.Add(new MetaDataStringTag("STREETNR", StreetNr));

						if (!string.IsNullOrEmpty(Building))
							MetaInfo.Add(new MetaDataStringTag("BLD", Building));

						if (!string.IsNullOrEmpty(Apartment))
							MetaInfo.Add(new MetaDataStringTag("APT", Apartment));

						if (!string.IsNullOrEmpty(Room))
							MetaInfo.Add(new MetaDataStringTag("ROOM", Room));

						if (!string.IsNullOrEmpty(Name))
							MetaInfo.Add(new MetaDataStringTag("NAME", Name));

						if (string.IsNullOrEmpty(ownerJid))
							await RegisterDevice(MetaInfo.ToArray());
						else
							await UpdateRegistration(MetaInfo.ToArray(), ownerJid);

						if (Updated)
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Country", Country);
							await RuntimeSettings.SetAsync("ThingRegistry.Region", Region);
							await RuntimeSettings.SetAsync("ThingRegistry.City", City);
							await RuntimeSettings.SetAsync("ThingRegistry.Area", Area);
							await RuntimeSettings.SetAsync("ThingRegistry.Street", Street);
							await RuntimeSettings.SetAsync("ThingRegistry.StreetNr", StreetNr);
							await RuntimeSettings.SetAsync("ThingRegistry.Building", Building);
							await RuntimeSettings.SetAsync("ThingRegistry.Apartment", Apartment);
							await RuntimeSettings.SetAsync("ThingRegistry.Room", Room);
							await RuntimeSettings.SetAsync("ThingRegistry.Name", Name);
						}
						break;
					}
					catch (Exception ex)
					{
						Log.Exception(ex);
					}
				}

				Country = InputString("Country", Country);
				Region = InputString("Region", Region);
				City = InputString("City", City);
				Area = InputString("Area", Area);
				Street = InputString("Street", Street);
				StreetNr = InputString("StreetNr", StreetNr);
				Building = InputString("Building", Building);
				Apartment = InputString("Apartment", Apartment);
				Room = InputString("Room", Room);
				Name = InputString("Name", Name);
				Updated = true;
				HasLocation = true;
			}
		}

		private static async Task RegisterDevice(MetaDataTag[] MetaInfo)
		{
			Log.Informational("Registering device.");

			string Key = await RuntimeSettings.GetAsync("ThingRegistry.Key", string.Empty);
			if (string.IsNullOrEmpty(Key))
			{
				byte[] Bin = new byte[32];
				using (RandomNumberGenerator Rnd = RandomNumberGenerator.Create())
				{
					Rnd.GetBytes(Bin);
				}

				Key = Hashes.BinaryToString(Bin);
				await RuntimeSettings.SetAsync("ThingRegistry.Key", Key);
			}

			int c = MetaInfo.Length;
			MetaDataTag[] MetaInfo2 = new MetaDataTag[c + 1];
			Array.Copy(MetaInfo, 0, MetaInfo2, 0, c);
			MetaInfo2[c] = new MetaDataStringTag("KEY", Key);

			registryClient?.RegisterThing(false, MetaInfo2, async (sender, e) =>
			{
				try
				{
					if (e.Ok)
					{
						await RuntimeSettings.SetAsync("ThingRegistry.Location", true);
						await RuntimeSettings.SetAsync("ThingRegistry.Owner", ownerJid = e.OwnerJid);

						if (string.IsNullOrEmpty(e.OwnerJid))
							Log.Informational("Registration successful.");
						else
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);
							Log.Informational("Registration updated. Device has an owner.",
								new KeyValuePair<string, object>("Owner", e.OwnerJid));

							MetaInfo2[c] = new MetaDataStringTag("JID", xmppClient?.BareJID);
						}

						if (xmppClient is not null)
							GenerateIoTDiscoUri(MetaInfo2, xmppClient.Host);
					}
					else
					{
						Log.Error("Registration failed.");
						await RegisterDevice();
					}
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			}, null);
		}

		private static void GenerateIoTDiscoUri(MetaDataTag[] MetaInfo, string Host)
		{
			string FilePath = Path.Combine(Environment.CurrentDirectory, "Sensor.iotdisco");
			string? DiscoUri = registryClient?.EncodeAsIoTDiscoURI(MetaInfo);
			QrMatrix? M = qrEncoder?.GenerateMatrix(CorrectionLevel.L, DiscoUri);
			string? QrCode = M?.ToQuarterBlockText();

			Log.Informational(QrCode);
			File.WriteAllText(FilePath, DiscoUri);

			StringBuilder sb = new();

			sb.AppendLine("[InternetShortcut]");
			sb.Append("URL=https://");
			sb.Append(Host);
			sb.Append("/QR/");
			sb.Append(HttpUtility.UrlEncode(DiscoUri));
			sb.AppendLine();

			File.WriteAllText(FilePath + ".url", sb.ToString());
		}

		private static async Task UpdateRegistration(MetaDataTag[] MetaInfo, string OwnerJid)
		{
			if (string.IsNullOrEmpty(OwnerJid))
				await RegisterDevice(MetaInfo);
			else
			{
				Log.Informational("Updating registration of device.",
					new KeyValuePair<string, object>("Owner", OwnerJid));

				registryClient?.UpdateThing(MetaInfo, async (sender, e) =>
				{
					try
					{
						if (e.Disowned)
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Owner", ownerJid = string.Empty);
							await RegisterDevice(MetaInfo);
						}
						else if (e.Ok)
						{
							Log.Informational("Registration update successful.");

							int c = MetaInfo.Length;
							MetaDataTag[] MetaInfo2 = new MetaDataTag[c + 1];
							Array.Copy(MetaInfo, 0, MetaInfo2, 0, c);
							MetaInfo2[c] = new MetaDataStringTag("JID", xmppClient?.BareJID);

							if (xmppClient is not null)
								GenerateIoTDiscoUri(MetaInfo2, xmppClient.Host);
						}
						else
						{
							Log.Error("Registration update failed.");
							await RegisterDevice(MetaInfo);
						}
					}
					catch (Exception ex)
					{
						Log.Exception(ex);
					}
				}, null);
			}
		}

		private static void Reregister()
		{
			Task _ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(5000);
					await RegisterDevice();
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			});
		}

		#endregion

		#region Sensor Data

		private static void SetupSensorServer()
		{
			SafeDispose(ref sensorServer);

			sensorServer = new SensorServer(xmppClient, provisioningClient, true);
			sensorServer.OnExecuteReadoutRequest += ReadSensor;

			//SafeDispose(ref pepClient);

			//pepClient = new PepClient(xmppClient);
		}

		private static async Task ReadSensor(object Sender, SensorDataServerRequest e)
		{
			try
			{
				Log.Informational("Performing readout.", xmppClient?.BareJID, e.Actor);

				List<Field> Fields = new();
				DateTime Now = DateTime.Now;
				bool ReadHistory = e.IsIncluded(FieldType.Historical);

				if (sampleTimer is null)
					Log.Notice("Sensor is disabled.");
				else
				{
					if (e.IsIncluded(FieldType.Identity))
					{
						Fields.Add(new StringField(ThingReference.Empty, Now, "Device ID", deviceId,
							FieldType.Identity, FieldQoS.AutomaticReadout));
					}

					if (weatherClient is not null)
						Fields.AddRange(await weatherClient.GetData());
				}

				await e.ReportFields(!ReadHistory, Fields);

				if (ReadHistory)
				{
					try
					{
						DateTime Last = await ReportHistory<PerMinute>(e, e.From, e.To);
						if (Last >= e.From && Last != DateTime.MinValue)
						{
							Last = await ReportHistory<PerQuarter>(e, e.From, Last);
							if (Last >= e.From && Last != DateTime.MinValue)
							{
								Last = await ReportHistory<PerHour>(e, e.From, Last);
								if (Last >= e.From && Last != DateTime.MinValue)
								{
									Last = await ReportHistory<PerDay>(e, e.From, Last);
									if (Last >= e.From && Last != DateTime.MinValue)
									{
										Last = await ReportHistory<PerWeek>(e, e.From, Last);
										if (Last >= e.From && Last != DateTime.MinValue)
											await ReportHistory<PerMonth>(e, e.From, Last);
									}
								}
							}
						}
					}
					finally
					{
						await e.ReportFields(true);
					}
				}
			}
			catch (Exception ex)
			{
				await e.ReportErrors(true, new ThingError(ThingReference.Empty, ex.Message));
			}
		}

		private static async Task<DateTime> ReportHistory<T>(SensorDataServerRequest e, DateTime From, DateTime To)
			where T : HistoricRecord
		{
			IEnumerable<T> Records = await Database.Find<T>(new FilterAnd(
				new FilterFieldGreaterOrEqualTo("Timestamp", From),
				new FilterFieldLesserOrEqualTo("Timestamp", To)), "-Timestamp");
			List<Field> ToReport = new();
			DateTime Last = DateTime.MinValue;

			foreach (T Rec in Records)
			{
				ToReport.Add(new QuantityField(ThingReference.Empty, Rec.Timestamp, Rec.FieldName, Rec.Magnitude, Rec.NrDecimals, Rec.Unit,
					FieldType.Historical, Rec.QoS));

				if (Rec is HistoricRecordWithPeaks WithPeaks)
				{
					ToReport.Add(new QuantityField(ThingReference.Empty, Rec.Timestamp, Rec.FieldName + ", Min", WithPeaks.MinMagnitude,
						WithPeaks.MinNrDecimals, WithPeaks.MinUnit, FieldType.Historical | FieldType.Peak, Rec.QoS));

					ToReport.Add(new QuantityField(ThingReference.Empty, Rec.Timestamp, Rec.FieldName + ", Max", WithPeaks.MaxMagnitude,
						WithPeaks.MaxNrDecimals, WithPeaks.MaxUnit, FieldType.Historical | FieldType.Peak, Rec.QoS));
				}

				if (ToReport.Count >= fieldBatchSize)
				{
					await e.ReportFields(false, ToReport.ToArray());
					ToReport.Clear();
				}

				Last = Rec.Timestamp;
			}

			if (ToReport.Count > 0)
			{
				await e.ReportFields(false, ToReport.ToArray());
				ToReport.Clear();
			}

			return Last;
		}

		private static async Task SetupSampleTimer()
		{
			ConfigEnabled(await GetEnabled());
		}

		public static Task<bool> GetEnabled()
		{
			return RuntimeSettings.GetAsync("OpenWeatherMap.Enabled", true);
		}

		public static async Task SetEnabled(bool Enabled)
		{
			await RuntimeSettings.SetAsync("OpenWeatherMap.Enabled", Enabled);
			ConfigEnabled(Enabled);
		}

		private static void ConfigEnabled(bool Enabled)
		{
			SafeDispose(ref sampleTimer);

			if (Enabled)
			{
				DateTime Now2 = DateTime.Now;
				sampleTimer = new Timer(SampleTimerElapsed, null,
					60000 - (Now2.Second * 1000) - Now2.Millisecond, 60000);
				Log.Notice("Enabling Sampling.");
			}
			else
				Log.Notice("Disabling Sampling.");
		}

		private static async void SampleTimerElapsed(object? P)
		{
			try
			{
				Log.Informational("Reading sensor.");

				if (xmppClient is not null && (xmppClient.State == XmppState.Error || xmppClient.State == XmppState.Offline))
					await xmppClient.Reconnect();

				if (weatherClient is not null)
				{
					Field[] Fields = await weatherClient.GetData();
					try
					{
						PublishMomentaryValues(Fields);
					}
					catch (Exception ex)
					{
						Log.Exception(ex);
					}

					await SaveHistory(Fields);
				}
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}

		private static void PublishMomentaryValues(params Field[] Fields)
		{
			/* Three methods to publish data using the Publish/Subscribe pattern exists:
			 * 
			 * 1) Using the presence stanza. In this chase, simply include the sensor data XML when you set your online presence:
			 * 
			 *       xmppClient.SetPresence(Availability.Chat, Xml.ToString());
			 *       
			 * 2) Using the Publish/Subscribe extension XEP-0060. In this case, you need to use an XMPP broker that supports XEP-0060, and
			 *    publish the sensor data XML to a node on the broke pubsub service. A subcriber receives the sensor data after successfully
			 *    subscribing to the node. You can use Waher.Networking.XMPP.PubSub to publish data using XEP-0060.
			 *    
			 * 3) Using the Personal Eventing Protocol (PEP) extension XEP-0163. It's a simplified Publish/Subscribe extension where each
			 *    account becomes its own Publish/Subsribe service. A contact with a presence subscription to the sensor, and showing an interest
			 *    in sensor data in its entity capabilities, will receive the data automatically. There's no need to manually subscibe to the data.
			 *    This approach is used in this example. You can use Waher.Networking.XMPP.PEP to publish data using XEP-0163.
			 *    
			 *    To receive personal events, register a personal event handler on the controller. This adds the required namespace to the
			 *    entity capability features list, and the broker will forward the events to you. Example:
			 *    
			 *       pepClient.RegisterHandler(typeof(SensorData), PepClient_SensorData);
			 *    
			 *    Then you process the incoming event as follows:
			 *    
			 *       private void PepClient_SensorData(object Sender, PersonalEventNotificationEventArgs e)
			 *       {
			 *          if (e.PersonalEvent is SensorData SensorData)
			 *          {
			 *             ...
			 *          }
			 *       }
			 *       
			 * You should also report read values to the sensor-data server object. It manages current event subscriptions made by
			 * peers. By regularly reporting new momentary values to the sensor-data server object, it maintains such event subscribers
			 * current, in accordance with individual event subcription rules.
			 */

			DateTime Now = DateTime.Now;

			//pepClient?.Publish(new SensorData(Fields), null, null);
			sensorServer?.NewMomentaryValues(Fields);

			lastPublished = Now;
		}

		#endregion

		#region Historical Data

		private static async Task SaveHistory(Field[] Fields)
		{
			DateTime TP = DateTime.Now;
			TP = TP.AddMilliseconds(-TP.Millisecond).AddSeconds(-TP.Second);

			// Minute records

			await Database.StartBulk();
			try
			{
				foreach (Field F in Fields)
				{
					if (F.Type == FieldType.Momentary && F is QuantityField Q)
					{
						PerMinute Rec = new()
						{
							FieldName = F.Name,
							QoS = F.QoS,
							Timestamp = F.Timestamp,
							Magnitude = Q.Value,
							NrDecimals = Q.NrDecimals,
							Unit = Q.Unit
						};

						await Database.Insert(Rec);
					}
				}

				await Database.FindDelete<PerMinute>(new FilterFieldLesserThan("Timestamp", TP.AddMinutes(-recordsInMemory)));
			}
			finally
			{
				await Database.EndBulk();
			}

			if (TP.Minute % 15 != 0)
				return;

			// Quarter records

			DateTime From = TP.AddMinutes(-15);
			IEnumerable<HistoricRecordWithPeaks> ToAdd;

			await Database.StartBulk();
			try
			{
				ToAdd = await CalcHistory<PerMinute, PerQuarter>(From, TP);
				await Database.Insert(ToAdd);
				await Database.FindDelete<PerQuarter>(new FilterFieldLesserThan("Timestamp", TP.AddMinutes(-recordsInMemory * 15)));
			}
			finally
			{
				await Database.EndBulk();
			}

			if (TP.Minute != 0)
				return;

			// Hour records

			From = TP.AddHours(-1);

			await Database.StartBulk();
			try
			{
				ToAdd = await CalcHistory<PerQuarter, PerHour>(From, TP);
				await Database.Insert(ToAdd);
				await Database.FindDelete<PerHour>(new FilterFieldLesserThan("Timestamp", TP.AddHours(-recordsInMemory)));
			}
			finally
			{
				await Database.EndBulk();
			}

			if (TP.Hour != 0)
				return;

			// Day records

			From = TP.AddDays(-1);

			await Database.StartBulk();
			try
			{
				ToAdd = await CalcHistory<PerHour, PerDay>(From, TP);
				await Database.Insert(ToAdd);
				await Database.FindDelete<PerDay>(new FilterFieldLesserThan("Timestamp", TP.AddDays(-recordsInMemory)));
			}
			finally
			{
				await Database.EndBulk();
			}

			if (TP.DayOfWeek == DayOfWeek.Monday)
			{
				// Week records

				From = TP.AddDays(-7);

				await Database.StartBulk();
				try
				{
					ToAdd = await CalcHistory<PerDay, PerWeek>(From, TP);
					await Database.Insert(ToAdd);
					await Database.FindDelete<PerWeek>(new FilterFieldLesserThan("Timestamp", TP.AddDays(-recordsInMemory * 7)));
				}
				finally
				{
					await Database.EndBulk();
				}
			}

			if (TP.Day != 1)
				return;

			// Month records

			From = TP.AddMonths(-1);

			await Database.StartBulk();
			try
			{
				ToAdd = await CalcHistory<PerDay, PerMonth>(From, TP);
				await Database.Insert(ToAdd);
				await Database.FindDelete<PerMonth>(new FilterFieldLesserThan("Timestamp", TP.AddMonths(-recordsInMemory)));
			}
			finally
			{
				await Database.EndBulk();
			}
		}

		private static async Task<IEnumerable<HistoricRecordWithPeaks>> CalcHistory<FromType, ToType>(DateTime FromInclusive, DateTime ToExclusive)
			where FromType : HistoricRecord
			where ToType : HistoricRecordWithPeaks, new()
		{
			IEnumerable<FromType> Records = await Database.Find<FromType>(new FilterAnd(
				new FilterFieldGreaterOrEqualTo("Timestamp", FromInclusive),
				new FilterFieldLesserThan("Timestamp", ToExclusive)));

			Dictionary<string, ToType> ByName = new();

			foreach (FromType Rec in Records)
			{
				if (ByName.TryGetValue(Rec.FieldName, out ToType? Stat))
				{
					if (Rec is HistoricRecordWithPeaks WithPeaks)
					{
						if (Compare(Stat.MinMagnitude, Stat.MinUnit, WithPeaks.MinMagnitude, WithPeaks.MinUnit) > 0)
						{
							Stat.MinMagnitude = WithPeaks.MinMagnitude;
							Stat.MinNrDecimals = WithPeaks.MinNrDecimals;
							Stat.MinUnit = WithPeaks.MinUnit;
							Stat.MinTimestamp = WithPeaks.MinTimestamp;
						}

						if (Compare(Stat.MaxMagnitude, Stat.MaxUnit, WithPeaks.MaxMagnitude, WithPeaks.MaxUnit) < 0)
						{
							Stat.MaxMagnitude = WithPeaks.MaxMagnitude;
							Stat.MaxNrDecimals = WithPeaks.MaxNrDecimals;
							Stat.MaxUnit = WithPeaks.MaxUnit;
							Stat.MaxTimestamp = WithPeaks.MaxTimestamp;
						}

						Stat.NrSamples += WithPeaks.NrSamples;
					}
					else
					{
						if (Compare(Stat.MinMagnitude, Stat.MinUnit, Rec.Magnitude, Rec.Unit) > 0)
						{
							Stat.MinMagnitude = Rec.Magnitude;
							Stat.MinNrDecimals = Rec.NrDecimals;
							Stat.MinUnit = Rec.Unit;
							Stat.MinTimestamp = Rec.Timestamp;
						}

						if (Compare(Stat.MaxMagnitude, Stat.MaxUnit, Rec.Magnitude, Rec.Unit) < 0)
						{
							Stat.MaxMagnitude = Rec.Magnitude;
							Stat.MaxNrDecimals = Rec.NrDecimals;
							Stat.MaxUnit = Rec.Unit;
							Stat.MaxTimestamp = Rec.Timestamp;
						}

						Stat.NrSamples++;
					}

					if (Stat.Unit == Rec.Unit)
					{
						Stat.Magnitude += Rec.Magnitude;
						Stat.NrDecimals = Math.Min(Stat.NrDecimals, Rec.NrDecimals);
						Stat.NrRecords++;
					}
					else
					{
						double? d = Add(Stat.Magnitude, Stat.Unit, Rec.Magnitude, Rec.Unit);
						if (d.HasValue)
						{
							Stat.Magnitude = d.Value;
							Stat.NrDecimals = Math.Min(Stat.NrDecimals, Rec.NrDecimals);
							Stat.NrRecords++;
						}
					}

					Stat.QoS = CalcMin(Stat.QoS, Rec.QoS);
				}
				else
				{
					if (Rec is HistoricRecordWithPeaks WithPeaks)
					{
						Stat = new ToType()
						{
							FieldName = Rec.FieldName,
							MinMagnitude = WithPeaks.MinMagnitude,
							MinNrDecimals = WithPeaks.MinNrDecimals,
							MinUnit = WithPeaks.MinUnit,
							MinTimestamp = WithPeaks.MinTimestamp,
							MaxMagnitude = WithPeaks.MaxMagnitude,
							MaxNrDecimals = WithPeaks.MaxNrDecimals,
							MaxUnit = WithPeaks.MaxUnit,
							MaxTimestamp = WithPeaks.MaxTimestamp,
							QoS = Rec.QoS,
							Timestamp = FromInclusive.Add(TimeSpan.FromDays(ToExclusive.Subtract(FromInclusive).TotalDays * 0.5)),
							Magnitude = Rec.Magnitude,
							NrDecimals = Rec.NrDecimals,
							Unit = Rec.Unit,
							NrRecords = 1,
							NrSamples = WithPeaks.NrSamples
						};
					}
					else
					{
						Stat = new ToType()
						{
							FieldName = Rec.FieldName,
							MinMagnitude = Rec.Magnitude,
							MinNrDecimals = Rec.NrDecimals,
							MinUnit = Rec.Unit,
							MinTimestamp = Rec.Timestamp,
							MaxMagnitude = Rec.Magnitude,
							MaxNrDecimals = Rec.NrDecimals,
							MaxUnit = Rec.Unit,
							MaxTimestamp = Rec.Timestamp,
							QoS = Rec.QoS,
							Timestamp = FromInclusive.Add(TimeSpan.FromDays(ToExclusive.Subtract(FromInclusive).TotalDays * 0.5)),
							Magnitude = Rec.Magnitude,
							NrDecimals = Rec.NrDecimals,
							Unit = Rec.Unit,
							NrRecords = 1,
							NrSamples = 1
						};
					}

					ByName[Rec.FieldName] = Stat;
				}

			}

			foreach (ToType Rec in ByName.Values)
			{
				if (Rec.NrRecords > 1)
					Rec.Magnitude /= Rec.NrRecords;
			}

			return ByName.Values;
		}

		private static int Compare(double Magnitude1, string Unit1, double Magnitude2, string Unit2)
		{
			if (Unit1 == Unit2)
				return Magnitude1.CompareTo(Magnitude2);

			if (!Unit.TryParse(Unit1, out Unit Parsed1))
				return 1;

			if (!Unit.TryParse(Unit2, out Unit Parsed2))
				return -1;

			if (!Unit.TryConvert(Magnitude2, Parsed2, Parsed1, out double Magnitude3))
				return -1;

			return Magnitude1.CompareTo(Magnitude3);
		}

		private static double? Add(double Magnitude1, string Unit1, double Magnitude2, string Unit2)
		{
			if (Unit1 == Unit2)
				return Magnitude1 + Magnitude2;

			if (!Unit.TryParse(Unit1, out Unit Parsed1))
				return null;

			if (!Unit.TryParse(Unit2, out Unit Parsed2))
				return null;

			if (!Unit.TryConvert(Magnitude2, Parsed2, Parsed1, out double Magnitude3))
				return null;

			return Magnitude1 + Magnitude3;
		}

		private const int qosComparableFlags = 0b10111000111111;
		private const int qosTransferrableFlags = 0b01000111000000;

		private static FieldQoS CalcMin(FieldQoS QoS1, FieldQoS QoS2)
		{
			int q1 = (int)QoS1;
			int q2 = (int)QoS2;
			int Result = Math.Min(q1 & qosComparableFlags, q2 & qosComparableFlags);

			Result |= q1 & qosTransferrableFlags;
			Result |= q2 & qosTransferrableFlags;

			return (FieldQoS)Result;
		}

		#endregion

		#region Actuator Control Parameters

		private static void SetupControlServer()
		{
			SafeDispose(ref controlServer);

			controlServer = new ControlServer(xmppClient, provisioningClient,
				new BooleanControlParameter("Enabled", "Operation", "Enabled", "If sensor is enabled.",
					async Node => await GetEnabled(),
					async (Node, Value) => await SetEnabled(Value)));
		}

		#endregion

		#region Chat Server

		private static void SetupChatServer()
		{
			SafeDispose(ref chatServer);

			bobClient ??= new BobClient(xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));
			chatServer = new ChatServer(xmppClient, bobClient, sensorServer, controlServer, provisioningClient);
		}

		#endregion
	}
}