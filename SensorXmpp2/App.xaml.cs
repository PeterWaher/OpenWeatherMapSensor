using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

using Waher.Content;
using Waher.Content.QR;
using Waher.Content.QR.Encoding;
using Waher.Content.Xml;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.BitsOfBinary;
using Waher.Networking.XMPP.Chat;
using Waher.Networking.XMPP.PEP;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Persistence.Serialization;
using Waher.Runtime.Inventory;
using Waher.Runtime.Settings;
using Waher.Security;
using Waher.Things;
using Waher.Things.SensorData;

namespace SensorXmpp
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	sealed partial class App : Application
	{
		private FilesProvider db = null;
		private Timer sampleTimer = null;
		private XmppClient xmppClient = null;
		private PepClient pepClient = null;

		private readonly QrEncoder qrEncoder = new QrEncoder();
		private string deviceId;
		private string thingRegistryJid = string.Empty;
		private string provisioningJid = string.Empty;
		private string ownerJid = string.Empty;
		private OpenWeatherMapApi weatherClient = null;
		private SensorServer sensorServer = null;
		private ThingRegistryClient registryClient = null;
		private ProvisioningClient provisioningClient = null;
		private BobClient bobClient = null;
		private ChatServer chatServer = null;
		private DateTime lastPublished = DateTime.MinValue;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
			this.Suspending += OnSuspending;
		}

		#region App UWP Life-Cycle

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="e">Details about the launch request and process.</param>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			// Do not repeat app initialization when the Window already has content,
			// just ensure that the window is active
			if (!(Window.Current.Content is Frame rootFrame))
			{
				// Create a Frame to act as the navigation context and navigate to the first page
				rootFrame = new Frame();

				rootFrame.NavigationFailed += OnNavigationFailed;

				if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
				{
					//TODO: Load state from previously suspended application
				}

				// Place the frame in the current Window
				Window.Current.Content = rootFrame;
			}

			if (e.PrelaunchActivated == false)
			{
				if (rootFrame.Content is null)
				{
					// When the navigation stack isn't restored navigate to the first page,
					// configuring the new page by passing required information as a navigation
					// parameter
					rootFrame.Navigate(typeof(MainPage), e.Arguments);
				}
				// Ensure the current window is active
				Window.Current.Activate();
				Task.Run((Action)this.Init);
			}
		}

		/// <summary>
		/// Invoked when Navigation to a certain page fails
		/// </summary>
		/// <param name="sender">The Frame which failed navigation</param>
		/// <param name="e">Details about the navigation failure</param>
		void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
		{
			throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
		}

		/// <summary>
		/// Invoked when application execution is being suspended.  Application state is saved
		/// without knowing whether the application will be terminated or resumed with the contents
		/// of memory still intact.
		/// </summary>
		/// <param name="sender">The source of the suspend request.</param>
		/// <param name="e">Details about the suspend request.</param>
		private void OnSuspending(object sender, SuspendingEventArgs e)
		{
			var deferral = e.SuspendingOperation.GetDeferral();

			this.sampleTimer?.Dispose();
			this.sampleTimer = null;

			this.pepClient?.Dispose();
			this.pepClient = null;

			this.chatServer?.Dispose();
			this.chatServer = null;

			this.bobClient?.Dispose();
			this.bobClient = null;

			this.sensorServer?.Dispose();
			this.sensorServer = null;

			this.xmppClient?.Dispose();
			this.xmppClient = null;

			db?.Stop()?.Wait();
			db?.Flush()?.Wait();

			Log.Terminate();

			deferral.Complete();
		}

		#endregion

		#region GUI synchronization

		public delegate Task GuiMethod();

		public static async Task RunGui(GuiMethod Callback)
		{
			TaskCompletionSource<bool> Result = new TaskCompletionSource<bool>();

			await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
			{
				try
				{
					await Callback();
					Result.TrySetResult(true);
				}
				catch (Exception ex)
				{
					Result.TrySetException(ex);
				}
			});

			await Result.Task;
		}

		public static Task Error(Exception ex)
		{
			Log.Critical(ex);
			return Error(ex.Message);
		}

		public static Task Error(string Message)
		{
			return MessageBox(Message, "Error");
		}

		public static async Task MessageBox(string Message, string Title)
		{
			MessageDialog Dialog = new MessageDialog(Message, Title);
			await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
				async () => await Dialog.ShowAsync());
		}

		#endregion

		#region Initialization of device

		private async void Init()
		{
			try
			{
				#region Initializing system

				Log.Informational("Starting application.");

				Types.Initialize(
					typeof(FilesProvider).GetTypeInfo().Assembly,
					typeof(ObjectSerializer).GetTypeInfo().Assembly,    // Waher.Persistence.Serialization was broken out of Waher.Persistence.FilesLW after the publishing of the MIoT book.
					typeof(RuntimeSettings).GetTypeInfo().Assembly,
					typeof(IContentEncoder).GetTypeInfo().Assembly,
					typeof(XmppClient).GetTypeInfo().Assembly,
					typeof(Waher.Content.Markdown.MarkdownDocument).GetTypeInfo().Assembly,
					typeof(XML).GetTypeInfo().Assembly,
					typeof(Waher.Script.Expression).GetTypeInfo().Assembly,
					typeof(Waher.Script.Graphs.Graph).GetTypeInfo().Assembly,
					typeof(Waher.Script.Persistence.SQL.Select).GetTypeInfo().Assembly,
					typeof(App).GetTypeInfo().Assembly);

				#endregion

				#region Setting up database

				db = await FilesProvider.CreateAsync(ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000);
				Database.Register(db);
				await db.RepairIfInproperShutdown(null);
				await db.Start();

				#endregion

				#region Device ID

				this.deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(this.deviceId))
				{
					this.deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", this.deviceId);
				}

				Log.Informational("Device ID: " + this.deviceId);

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
							this.weatherClient = new OpenWeatherMapApi(ApiKey, Location, Country);
							await this.weatherClient.GetData(); // Test parameters

							if (Updated)
							{
								await RuntimeSettings.SetAsync("OpenWeatherMap.ApiKey", ApiKey);
								await RuntimeSettings.SetAsync("OpenWeatherMap.Location", Location);
								await RuntimeSettings.SetAsync("OpenWeatherMap.Country", Country);
							}

							break;
						}
						catch (Exception ex)
						{
							await Error(ex);
						}
					}

					await RunGui(async () =>
					{
						OpenWeatherMapDialog Dialog = new OpenWeatherMapDialog(ApiKey, Location, Country);
						if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
						{
							ApiKey = Dialog.Key;
							Location = Dialog.Location;
							Country = Dialog.CountryCode;
							Updated = true;
						}
					});
				}

				#endregion

				#region XMPP Connection
				
				string Host = await RuntimeSettings.GetAsync("XmppHost", "waher.se");
				int Port = (int)await RuntimeSettings.GetAsync("XmppPort", 5222);
				string UserName = await RuntimeSettings.GetAsync("XmppUserName", string.Empty);
				string PasswordHash = await RuntimeSettings.GetAsync("XmppPasswordHash", string.Empty);
				string PasswordHashMethod = await RuntimeSettings.GetAsync("XmppPasswordHashMethod", string.Empty);
				bool ConnectionEstablished = false;

				Updated = false;

				while (true)
				{
					if (!string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(PasswordHash))
					{
						try
						{
							this.xmppClient?.Dispose();
							this.xmppClient = null;

							if (string.IsNullOrEmpty(PasswordHashMethod))
								this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, "en", typeof(App).GetTypeInfo().Assembly);
							else
								this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en", typeof(App).GetTypeInfo().Assembly);

							this.xmppClient.AllowCramMD5 = false;
							this.xmppClient.AllowDigestMD5 = false;
							this.xmppClient.AllowPlain = false;
							this.xmppClient.AllowScramSHA1 = true;
							this.xmppClient.AllowScramSHA256 = true;

							this.xmppClient.AllowRegistration();                /* Allows registration on servers that do not require signatures. */
							// this.xmppClient.AllowRegistration(Key, Secret);	/* Allows registration on servers requiring a signature of the registration request. */

							this.xmppClient.OnStateChanged += (sender, State) =>
							{
								Log.Informational("Changing state: " + State.ToString());

								switch (State)
								{
									case XmppState.Connected:
										Log.Informational("Connected as " + this.xmppClient.FullJID);
										ConnectionEstablished = true;
										break;

									case XmppState.Error:
									case XmppState.Offline:
										if (ConnectionEstablished)
											this.xmppClient.Reconnect();

										ConnectionEstablished = false;
										break;
								}

								return Task.CompletedTask;
							};

							this.xmppClient.OnConnectionError += (sender, ex) =>
							{
								Log.Error(ex.Message);
								return Task.CompletedTask;
							};

							Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
							this.xmppClient.Connect();

							switch (await this.xmppClient.WaitStateAsync(10000, XmppState.Connected, XmppState.Error, XmppState.Offline))
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
								await RuntimeSettings.SetAsync("XmppHost", Host = this.xmppClient.Host);
								await RuntimeSettings.SetAsync("XmppPort", Port = this.xmppClient.Port);
								await RuntimeSettings.SetAsync("XmppUserName", UserName = this.xmppClient.UserName);
								await RuntimeSettings.SetAsync("XmppPasswordHash", PasswordHash = this.xmppClient.PasswordHash);
								await RuntimeSettings.SetAsync("XmppPasswordHashMethod", PasswordHashMethod = this.xmppClient.PasswordHashMethod);
							}

							break;
						}
						catch (Exception ex)
						{
							await Error(ex);
						}
					}

					await RunGui(async () =>
					{
						AccountDialog Dialog = new AccountDialog(Host, Port, UserName);
						if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
						{
							Host = Dialog.Host;
							Port = Dialog.Port;
							UserName = Dialog.UserName;
							PasswordHash = Dialog.Password;
							PasswordHashMethod = string.Empty;
							Updated = true;
						}
					});
				}

				#endregion

				#region Basic XMPP events & features

				this.xmppClient.OnError += (Sender, ex) =>
				{
					Log.Error(ex);
					return Task.CompletedTask;
				};

				this.xmppClient.OnPasswordChanged += (Sender, e) => Log.Informational("Password changed.", this.xmppClient.BareJID);

				this.xmppClient.OnPresenceSubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship request accepted.", this.xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				this.xmppClient.OnPresenceUnsubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship removal accepted.", this.xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				await this.RegisterVCard();

				#endregion

				#region Configuring Decision Support & Provisioning

				this.thingRegistryJid = await RuntimeSettings.GetAsync("ThingRegistry.JID", string.Empty);
				this.provisioningJid = await RuntimeSettings.GetAsync("ProvisioningServer.JID", this.thingRegistryJid);
				this.ownerJid = await RuntimeSettings.GetAsync("ThingRegistry.Owner", string.Empty);

				if (string.IsNullOrEmpty(this.thingRegistryJid) || string.IsNullOrEmpty(this.provisioningJid))
				{
					Log.Informational("Searching for Thing Registry and Provisioning Server.");

					ServiceItemsDiscoveryEventArgs e = await this.xmppClient.ServiceItemsDiscoveryAsync(this.xmppClient.Domain);
					foreach (Item Item in e.Items)
					{
						ServiceDiscoveryEventArgs e2 = await this.xmppClient.ServiceDiscoveryAsync(Item.JID);

						try
						{
							if (e2.HasFeature(ProvisioningClient.NamespaceProvisioningDevice))
							{
								await RuntimeSettings.SetAsync("ProvisioningServer.JID", this.provisioningJid = Item.JID);
								Log.Informational("Provisioning server found.", this.provisioningJid);
							}

							if (e2.HasFeature(ThingRegistryClient.NamespaceDiscovery))
							{
								await RuntimeSettings.SetAsync("ThingRegistry.JID", this.thingRegistryJid = Item.JID);
								Log.Informational("Thing registry found.", this.thingRegistryJid);
							}
						}
						catch (Exception ex)
						{
							Log.Critical(ex);
						}
					}
				}

				if (!string.IsNullOrEmpty(this.provisioningJid))
					this.provisioningClient = new ProvisioningClient(this.xmppClient, this.provisioningJid, this.ownerJid);

				if (!string.IsNullOrEmpty(this.thingRegistryJid))
				{
					this.registryClient = new ThingRegistryClient(this.xmppClient, this.thingRegistryJid);

					this.registryClient.Claimed += async (sender, e) =>
					{
						try
						{
							Log.Notice("Owner claimed device.", string.Empty, e.JID);

							await RuntimeSettings.SetAsync("ThingRegistry.Owner", this.ownerJid = e.JID);
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);
						}
						catch (Exception ex)
						{
							Log.Critical(ex);
						}
					};

					this.registryClient.Disowned += async (sender, e) =>
					{
						try
						{
							Log.Notice("Owner disowned device.", string.Empty, this.ownerJid);

							await RuntimeSettings.SetAsync("ThingRegistry.Owner", this.ownerJid = string.Empty);
							await this.RegisterDevice();
						}
						catch (Exception ex)
						{
							Log.Critical(ex);
						}
					};
				}

				await this.RegisterDevice();

				#endregion

				this.SetupSensorServer();
			}
			catch (Exception ex)
			{
				await Error(ex);
			}
		}

		#endregion

		#region vCard contact information for device

		// XEP-0054 - vcard-temp: http://xmpp.org/extensions/xep-0054.html

		private async Task RegisterVCard()
		{
			this.xmppClient.RegisterIqGetHandler("vCard", "vcard-temp", async (sender, e) =>
			{
				e.IqResult(await this.GetVCardXml());
			}, true);

			Log.Informational("Setting vCard");
			this.xmppClient.SendIqSet(string.Empty, await this.GetVCardXml(), (sender, e) =>
			{
				if (e.Ok)
					Log.Informational("vCard successfully set.");
				else
					Log.Error("Unable to set vCard.");

				return Task.CompletedTask;

			}, null);
		}

		private async Task<string> GetVCardXml()
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<vCard xmlns='vcard-temp'>");
			Xml.Append("<FN>Open Weather Map Sensor</FN><N><FAMILY>Sensor</FAMILY><GIVEN>Open Weather Map</GIVEN><MIDDLE/></N>");
			Xml.Append("<URL>https://github.com/PeterWaher/OpenWeatherMapSensor</URL>");
			Xml.Append("<JABBERID>");
			Xml.Append(XML.Encode(this.xmppClient.BareJID));
			Xml.Append("</JABBERID>");
			Xml.Append("<UID>");
			Xml.Append(this.deviceId);
			Xml.Append("</UID>");
			Xml.Append("<DESC>XMPP Sensor Project (OpenWeatherMapSensor), based on a sensor example from the book Mastering Internet of Things, by Peter Waher.</DESC>");

			// XEP-0153 - vCard-Based Avatars: http://xmpp.org/extensions/xep-0153.html

			StorageFile File = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/LargeTile.scale-100.png"));
			byte[] Icon = System.IO.File.ReadAllBytes(File.Path);

			Xml.Append("<PHOTO><TYPE>image/png</TYPE><BINVAL>");
			Xml.Append(Convert.ToBase64String(Icon));
			Xml.Append("</BINVAL></PHOTO>");
			Xml.Append("</vCard>");

			return Xml.ToString();
		}

		#endregion

		#region Device Registration

		private async Task RegisterDevice()
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
						List<MetaDataTag> MetaInfo = new List<MetaDataTag>()
						{
							new MetaDataStringTag("CLASS", "Sensor"),
							new MetaDataStringTag("TYPE", "Open Weather Map"),
							new MetaDataStringTag("MAN", "waher.se"),
							new MetaDataStringTag("MODEL", "OpenWeatherMapSensor"),
							new MetaDataStringTag("PURL", "https://github.com/PeterWaher/OpenWeatherMapSensor"),
							new MetaDataStringTag("SN", this.deviceId),
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

						if (string.IsNullOrEmpty(this.ownerJid))
							await this.RegisterDevice(MetaInfo.ToArray());
						else
							await this.UpdateRegistration(MetaInfo.ToArray(), this.ownerJid);

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
						await Error(ex);
					}
				}

				await RunGui(async () =>
				{
					RegistrationDialog Dialog = new RegistrationDialog();

					if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
					{
						Country = Dialog.Reg_Country;
						Region = Dialog.Reg_Region;
						City = Dialog.Reg_City;
						Area = Dialog.Reg_Area;
						Street = Dialog.Reg_Street;
						StreetNr = Dialog.Reg_StreetNr;
						Building = Dialog.Reg_Building;
						Apartment = Dialog.Reg_Apartment;
						Room = Dialog.Reg_Room;
						Name = Dialog.Name;

						Updated = true;
						HasLocation = true;
					}
				});
			}
		}

		private async Task RegisterDevice(MetaDataTag[] MetaInfo)
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

			this.registryClient.RegisterThing(false, MetaInfo2, async (sender, e) =>
			{
				try
				{
					if (e.Ok)
					{
						await RuntimeSettings.SetAsync("ThingRegistry.Location", true);
						await RuntimeSettings.SetAsync("ThingRegistry.Owner", this.ownerJid = e.OwnerJid);

						if (string.IsNullOrEmpty(e.OwnerJid))
							Log.Informational("Registration successful.");
						else
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);
							Log.Informational("Registration updated. Device has an owner.",
								new KeyValuePair<string, object>("Owner", e.OwnerJid));

							MetaInfo2[c] = new MetaDataStringTag("JID", this.xmppClient.BareJID);
						}

						this.GenerateIoTDiscoUri(MetaInfo2);
					}
					else
					{
						Log.Error("Registration failed.");
						await this.RegisterDevice();
					}
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			}, null);
		}

		private void GenerateIoTDiscoUri(MetaDataTag[] MetaInfo)
		{
			string FilePath = ApplicationData.Current.LocalFolder.Path + Path.DirectorySeparatorChar + "Sensor.iotdisco";
			string DiscoUri = registryClient.EncodeAsIoTDiscoURI(MetaInfo);
			QrMatrix M = qrEncoder.GenerateMatrix(CorrectionLevel.L, DiscoUri);
			byte[] Pixels = M.ToRGBA(300, 300);

			MainPage.Instance.AddLogMessage(Pixels, 300, 300);

			File.WriteAllText(FilePath, DiscoUri);
		}

		private async Task UpdateRegistration(MetaDataTag[] MetaInfo, string OwnerJid)
		{
			if (string.IsNullOrEmpty(OwnerJid))
				await this.RegisterDevice(MetaInfo);
			else
			{
				Log.Informational("Updating registration of device.",
					new KeyValuePair<string, object>("Owner", OwnerJid));

				this.registryClient.UpdateThing(MetaInfo, async (sender, e) =>
				{
					try
					{
						if (e.Disowned)
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Owner", this.ownerJid = string.Empty);
							await this.RegisterDevice(MetaInfo);
						}
						else if (e.Ok)
						{
							Log.Informational("Registration update successful.");

							int c = MetaInfo.Length;
							MetaDataTag[] MetaInfo2 = new MetaDataTag[c + 1];
							Array.Copy(MetaInfo, 0, MetaInfo2, 0, c);
							MetaInfo2[c] = new MetaDataStringTag("JID", this.xmppClient.BareJID);

							this.GenerateIoTDiscoUri(MetaInfo2);
						}
						else
						{
							Log.Error("Registration update failed.");
							await this.RegisterDevice(MetaInfo);
						}
					}
					catch (Exception ex)
					{
						Log.Critical(ex);
					}
				}, null);
			}
		}

		#endregion

		#region Sensor Data

		private void SetupSensorServer()
		{
			this.sensorServer?.Dispose();
			this.sensorServer = null;

			this.sensorServer = new SensorServer(this.xmppClient, this.provisioningClient, true);
			this.sensorServer.OnExecuteReadoutRequest += async (sender, e) =>
			{
				try
				{
					Log.Informational("Performing readout.", this.xmppClient.BareJID, e.Actor);

					List<Field> Fields = new List<Field>();
					DateTime Now = DateTime.Now;

					if (e.IsIncluded(FieldType.Identity))
						Fields.Add(new StringField(ThingReference.Empty, Now, "Device ID", this.deviceId, FieldType.Identity, FieldQoS.AutomaticReadout));

					Fields.AddRange(await this.weatherClient.GetData());

					e.ReportFields(true, Fields);
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			};

			if (this.bobClient is null)
				this.bobClient = new BobClient(this.xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));

			this.pepClient?.Dispose();
			this.pepClient = null;

			this.chatServer?.Dispose();
			this.chatServer = null;

			this.chatServer = new ChatServer(this.xmppClient, this.bobClient, this.sensorServer, this.provisioningClient);
			this.pepClient = new PepClient(this.xmppClient);

			DateTime Now2 = DateTime.Now;
			this.sampleTimer = new Timer(this.SampleTimerElapsed, null, 60000 - (Now2.Second * 1000) - Now2.Millisecond, 60000);
		}

		private async void SampleTimerElapsed(object P)
		{
			try
			{
				if (this.xmppClient.State == XmppState.Error || this.xmppClient.State == XmppState.Offline)
					this.xmppClient.Reconnect();

				Field[] Fields = await this.weatherClient.GetData();

				this.PublishMomentaryValues(Fields);
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		private void PublishMomentaryValues(params Field[] Fields)
		{
			/* Three methods to publish data using the Publish/Subscribe pattern exists:
			 * 
			 * 1) Using the presence stanza. In this chase, simply include the sensor data XML when you set your online presence:
			 * 
			 *       this.xmppClient.SetPresence(Availability.Chat, Xml.ToString());
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
			 *       this.pepClient.RegisterHandler(typeof(SensorData), PepClient_SensorData);
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

			this.pepClient?.Publish(new SensorData(Fields), null, null);
			this.sensorServer?.NewMomentaryValues(Fields);

			this.lastPublished = Now;
		}

		#endregion
	}
}
