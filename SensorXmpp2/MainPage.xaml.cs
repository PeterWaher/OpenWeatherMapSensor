using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Waher.Events;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace SensorXmpp
{
	/// <summary>
	/// An empty page that can be used on its own or navigated to within a Frame.
	/// </summary>
	public sealed partial class MainPage : Page
	{
		private static MainPage instance = null;
		private Events events;

		public MainPage()
		{
			this.InitializeComponent();

			this.events = new MainPage.Events();
			Log.Register(this.events);

			if (instance is null)
				instance = this;
		}

		private void Page_Unloaded(object sender, RoutedEventArgs e)
		{
			Log.Unregister(this.events);
			this.events = null;

			if (instance == this)
				instance = null;
		}

		public static MainPage Instance
		{
			get { return instance; }
		}

		public async void AddLogMessage(string Message)
		{
			await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
			{
				this.AddLogMessageLocked(new TextBlock() 
				{ 
					Text = Message, 
					TextWrapping = TextWrapping.Wrap 
				});
			});
		}

		private void AddLogMessageLocked(UIElement Control)
		{
			this.EventsPanel.Children.Insert(0, Control);

			while (this.EventsPanel.Children.Count > 1000)
				this.EventsPanel.Children.RemoveAt(1000);
		}

		public async void AddLogMessage(byte[] Pixels, int Width, int Height)
		{
			await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
			{
				WriteableBitmap Bmp = new WriteableBitmap(Width, Height);

				using (Stream Buffer = Bmp.PixelBuffer.AsStream())
				{
					Buffer.Write(Pixels, 0, Pixels.Length);
				}

				this.AddLogMessageLocked(new Image() 
				{ 
					Source = Bmp, 
					Stretch = Stretch.None 
				});
			});
		}

		private class Events : EventSink
		{
			public Events() : base(string.Empty)
			{
			}

			public override Task Queue(Event Event)
			{
				StringBuilder sb = new StringBuilder(Event.Message);

				if (!string.IsNullOrEmpty(Event.Object))
				{
					sb.Append(' ');
					sb.Append(Event.Object);
				}

				if (!string.IsNullOrEmpty(Event.Actor))
				{
					sb.Append(' ');
					sb.Append(Event.Actor);
				}

				foreach (KeyValuePair<string, object> Parameter in Event.Tags)
				{
					sb.Append(" [");
					sb.Append(Parameter.Key);
					sb.Append("=");
					if (Parameter.Value != null)
						sb.Append(Parameter.Value.ToString());
					sb.Append("]");
				}

				if (Event.Type >= EventType.Critical && !string.IsNullOrEmpty(Event.StackTrace))
				{
					sb.Append("\r\n\r\n");
					sb.Append(Event.StackTrace);
				}

				MainPage.Instance.AddLogMessage(sb.ToString());

				return Task.CompletedTask;
			}
		}

	}
}
