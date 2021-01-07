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
		private bool newXmppClient = true;

		private string deviceId;
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

		#region UWP interface

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

		private async void Init()
		{
			try
			{
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

				db = await FilesProvider.CreateAsync(ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000);
				Database.Register(db);
				await db.RepairIfInproperShutdown(null);
				await db.Start();

				this.deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(this.deviceId))
				{
					this.deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", this.deviceId);
				}

				Log.Informational("Device ID: " + this.deviceId);

				string ApiKey = await RuntimeSettings.GetAsync("OpenWeatherMap.ApiKey", string.Empty);
				string Location = await RuntimeSettings.GetAsync("OpenWeatherMap.Location", string.Empty);
				string Country = await RuntimeSettings.GetAsync("OpenWeatherMap.Country", string.Empty);
				bool Updated = false;

				// Open Weather Map settings:

				while (true)
				{
					if (!string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(Location) && !string.IsNullOrEmpty(Country))
					{
						try
						{
							await OpenWeatherMapApi.GetData(ApiKey, Location, Country);

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

				// XMPP connection:

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
							this.xmppClient?.Dispose();
							this.xmppClient = null;

							this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en", typeof(App).GetTypeInfo().Assembly)
							{
								AllowCramMD5 = false,
								AllowDigestMD5 = false,
								AllowPlain = false,
								AllowScramSHA1 = true,
								AllowScramSHA256 = true
							};

							this.xmppClient.AllowRegistration();                /* Allows registration on servers that do not require signatures. */
							// this.xmppClient.AllowRegistration(Key, Secret);	/* Allows registration on servers requiring a signature of the registration request. */

							this.xmppClient.OnStateChanged += (sender, State) =>
							{
								Log.Informational("Changing state: " + State.ToString());

								if (State == XmppState.Connected)
									Log.Informational("Connected as " + this.xmppClient.FullJID);

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

				// Setting up device

				await this.SetVCard();
				await this.RegisterDevice();
			}
			catch (Exception ex)
			{
				await Error(ex);
			}
		}

		private void AttachFeatures()
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

					// TODO: Do Readout

					e.ReportFields(true, Fields);
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			};

			if (this.newXmppClient)
			{
				this.newXmppClient = false;

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

				if (this.bobClient is null)
					this.bobClient = new BobClient(this.xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));

				this.pepClient?.Dispose();
				this.pepClient = null;

				this.chatServer?.Dispose();
				this.chatServer = null;

				this.chatServer = new ChatServer(this.xmppClient, this.bobClient, this.sensorServer, this.provisioningClient);
				this.pepClient = new PepClient(this.xmppClient);

				// XEP-0054: vcard-temp: http://xmpp.org/extensions/xep-0054.html
				this.xmppClient.RegisterIqGetHandler("vCard", "vcard-temp", this.QueryVCardHandler, true);
			}
		}

		private void PublishMomentaryValues()
		{
			if (this.xmppClient is null || this.xmppClient.State != XmppState.Connected)
				return;

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
			 */

			DateTime Now = DateTime.Now;

			this.pepClient?.Publish(new SensorData(), null, null);

			this.lastPublished = Now;
		}

		private async Task QueryVCardHandler(object Sender, IqEventArgs e)
		{
			e.IqResult(await this.GetVCardXml());
		}

		private async Task SetVCard()
		{
			Log.Informational("Setting vCard");

			// XEP-0054 - vcard-temp: http://xmpp.org/extensions/xep-0054.html

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

		private async void SampleValues(object State)
		{
			try
			{
				DateTime Timestamp = DateTime.Now;

				if (Timestamp.Second == 0 && this.xmppClient != null &&
					(this.xmppClient.State == XmppState.Error || this.xmppClient.State == XmppState.Offline))
				{
					this.xmppClient.Reconnect();
				}
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		private async Task RegisterDevice()
		{
			string ThingRegistryJid = await RuntimeSettings.GetAsync("ThingRegistry.JID", string.Empty);
			string ProvisioningJid = await RuntimeSettings.GetAsync("ProvisioningServer.JID", ThingRegistryJid);
			string OwnerJid = await RuntimeSettings.GetAsync("ThingRegistry.Owner", string.Empty);

			if (!string.IsNullOrEmpty(ThingRegistryJid) && !string.IsNullOrEmpty(ProvisioningJid))
			{
				this.UseProvisioningServer(ProvisioningJid, OwnerJid);
				await this.RegisterDevice(ThingRegistryJid, OwnerJid);
			}
			else
			{
				Log.Informational("Searching for Thing Registry and Provisioning Server.");

				this.xmppClient.SendServiceItemsDiscoveryRequest(this.xmppClient.Domain, (sender, e) =>
				{
					foreach (Item Item in e.Items)
					{
						this.xmppClient.SendServiceDiscoveryRequest(Item.JID, async (sender2, e2) =>
						{
							try
							{
								Item Item2 = (Item)e2.State;

								if (e2.HasFeature(ProvisioningClient.NamespaceProvisioningDevice))
								{
									Log.Informational("Provisioning server found.", Item2.JID);
									this.UseProvisioningServer(Item2.JID, OwnerJid);
									await RuntimeSettings.SetAsync("ProvisioningServer.JID", Item2.JID);
								}

								if (e2.HasFeature(ThingRegistryClient.NamespaceDiscovery))
								{
									Log.Informational("Thing registry found.", Item2.JID);

									await RuntimeSettings.SetAsync("ThingRegistry.JID", Item2.JID);
									await this.RegisterDevice(Item2.JID, OwnerJid);
								}
							}
							catch (Exception ex)
							{
								Log.Critical(ex);
							}
						}, Item);
					}

					return Task.CompletedTask;

				}, null);
			}
		}

		private void UseProvisioningServer(string JID, string OwnerJid)
		{
			if (this.provisioningClient is null ||
				this.provisioningClient.ProvisioningServerAddress != JID ||
				this.provisioningClient.OwnerJid != OwnerJid)
			{
				this.provisioningClient?.Dispose();
				this.provisioningClient = null;

				this.provisioningClient = new ProvisioningClient(this.xmppClient, JID, OwnerJid);
				this.AttachFeatures();
			}
		}

		private async Task RegisterDevice(string RegistryJid, string OwnerJid)
		{
			if (this.registryClient is null || this.registryClient.ThingRegistryAddress != RegistryJid)
			{
				this.registryClient?.Dispose();
				this.registryClient = null;

				this.registryClient = new ThingRegistryClient(this.xmppClient, RegistryJid);

				this.registryClient.Claimed += async (sender, e) =>
				{
					try
					{
						await RuntimeSettings.SetAsync("ThingRegistry.Owner", e.JID);
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
						await RuntimeSettings.SetAsync("ThingRegistry.Owner", string.Empty);
						await this.RegisterDevice();
					}
					catch (Exception ex)
					{
						Log.Critical(ex);
					}
				};
			}

			string s;
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

			if (await RuntimeSettings.GetAsync("ThingRegistry.Location", false))
			{
				s = await RuntimeSettings.GetAsync("ThingRegistry.Country", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("COUNTRY", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Region", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("REGION", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.City", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("CITY", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Area", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("AREA", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Street", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("STREET", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.StreetNr", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("STREETNR", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Building", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("BLD", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Apartment", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("APT", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Room", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("ROOM", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Name", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("NAME", s));

				await this.UpdateRegistration(MetaInfo.ToArray(), OwnerJid);
			}
			else
			{
				try
				{
					await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
					{
						try
						{
							RegistrationDialog Dialog = new RegistrationDialog();

							switch (await Dialog.ShowAsync())
							{
								case ContentDialogResult.Primary:
									await RuntimeSettings.SetAsync("ThingRegistry.Country", s = Dialog.Reg_Country);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("COUNTRY", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Region", s = Dialog.Reg_Region);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("REGION", s));

									await RuntimeSettings.SetAsync("ThingRegistry.City", s = Dialog.Reg_City);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("CITY", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Area", s = Dialog.Reg_Area);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("AREA", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Street", s = Dialog.Reg_Street);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("STREET", s));

									await RuntimeSettings.SetAsync("ThingRegistry.StreetNr", s = Dialog.Reg_StreetNr);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("STREETNR", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Building", s = Dialog.Reg_Building);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("BLD", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Apartment", s = Dialog.Reg_Apartment);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("APT", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Room", s = Dialog.Reg_Room);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("ROOM", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Name", s = Dialog.Name);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("NAME", s));

									await this.RegisterDevice(MetaInfo.ToArray());
									break;

								case ContentDialogResult.Secondary:
									await this.RegisterDevice();
									break;
							}
						}
						catch (Exception ex)
						{
							Log.Critical(ex);
						}
					});
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
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
			Array.Resize<MetaDataTag>(ref MetaInfo, c + 1);
			MetaInfo[c] = new MetaDataStringTag("KEY", Key);

			this.registryClient.RegisterThing(false, MetaInfo, async (sender, e) =>
			{
				try
				{
					if (e.Ok)
					{
						await RuntimeSettings.SetAsync("ThingRegistry.Location", true);
						await RuntimeSettings.SetAsync("ThingRegistry.Owner", e.OwnerJid);

						if (string.IsNullOrEmpty(e.OwnerJid))
						{
							string ClaimUrl = registryClient.EncodeAsIoTDiscoURI(MetaInfo);
							string FilePath = ApplicationData.Current.LocalFolder.Path + Path.DirectorySeparatorChar + "Sensor.iotdisco";

							Log.Informational("Registration successful.");
							Log.Informational(ClaimUrl, new KeyValuePair<string, object>("Path", FilePath));

							File.WriteAllText(FilePath, ClaimUrl);
						}
						else
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);
							Log.Informational("Registration updated. Device has an owner.", new KeyValuePair<string, object>("Owner", e.OwnerJid));
						}
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
							await RuntimeSettings.SetAsync("ThingRegistry.Owner", string.Empty);
							await this.RegisterDevice(MetaInfo);
						}
						else if (e.Ok)
							Log.Informational("Registration update successful.");
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

			this.sampleTimer?.Dispose();
			this.sampleTimer = null;

			db?.Stop()?.Wait();
			db?.Flush()?.Wait();

			Log.Terminate();

			deferral.Complete();
		}
	}
}
