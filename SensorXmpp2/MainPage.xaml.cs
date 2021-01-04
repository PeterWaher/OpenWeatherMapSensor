﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
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
		private StreamWriter eventOutput = null;
		private bool eventFirst;
		private Events events;

		public MainPage()
		{
			this.InitializeComponent();

			this.events = new MainPage.Events();
			Log.Register(this.events);

			if (instance is null)
				instance = this;

			Hyperlink Link = new Hyperlink();

			Link.Inlines.Add(new Run()
			{
				Text = Windows.Storage.ApplicationData.Current.LocalFolder.Path
			});

			Link.Click += Link_Click;

			ToolTip ToolTip = new ToolTip()
			{
				Content = "If provisioning is used, you will find the iotdisco URI in this folder. Use this URI to claim the device. " +
					"Erase content of this folder when application is closed, and then restart, to reconfigure the application."
			};

			ToolTipService.SetToolTip(Link, ToolTip);

			this.LocalFolder.Inlines.Add(new Run() { Text = " " });
			this.LocalFolder.Inlines.Add(Link);
		}

		private async void Link_Click(Hyperlink sender, HyperlinkClickEventArgs args)
		{
			try
			{
				await Windows.System.Launcher.LaunchFolderAsync(Windows.Storage.ApplicationData.Current.LocalFolder);
			}
			catch (Exception ex)
			{
				await App.Error(ex);
			}
		}

		private void Page_Unloaded(object sender, RoutedEventArgs e)
		{
			Log.Unregister(this.events);
			this.events = null;

			if (instance == this)
				instance = null;

			this.CloseFiles();
		}

		public static MainPage Instance
		{
			get { return instance; }
		}

		public async void AddLogMessage(string Message)
		{
			DateTime TP = DateTime.Now;
			StreamWriter w;

			await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
			{
				this.EventsPanel.Children.Insert(0, new TextBlock() { Text = Message, TextWrapping = TextWrapping.Wrap });

				while (this.EventsPanel.Children.Count > 1000)
					this.EventsPanel.Children.RemoveAt(1000);
			});

			if ((w = this.eventOutput) != null)
			{
				lock (w)
				{
					DateTime PrevLog = DateTime.MinValue;	// Makes sure all events are logged.

					if (!this.WriteNewRecord(w, TP, ref PrevLog, ref this.eventFirst))
						return;

					w.Write("\"");
					w.Write(Message);
					w.Write("\"]");
				}
			}
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

		private bool WriteNewRecord(StreamWriter w, DateTime TP, ref DateTime Prev, ref bool First)
		{
			if (First)
			{
				First = false;
				w.Write("[[");
			}
			else
			{
				if (TP.Year == Prev.Year &&
					TP.Month == Prev.Month &&
					TP.Day == Prev.Day &&
					TP.Hour == Prev.Hour &&
					TP.Minute == Prev.Minute &&
					TP.Second == Prev.Second &&
					TP.Millisecond == Prev.Millisecond)
				{
					return false;
				}

				w.WriteLine(",");
				w.Write(" [");
			}

			Prev = TP;

			w.Write("DateTime(");
			w.Write(TP.Year.ToString("D4"));
			w.Write(",");
			w.Write(TP.Month.ToString("D2"));
			w.Write(",");
			w.Write(TP.Day.ToString("D2"));
			w.Write(",");
			w.Write(TP.Hour.ToString("D2"));
			w.Write(",");
			w.Write(TP.Minute.ToString("D2"));
			w.Write(",");
			w.Write(TP.Second.ToString("D2"));
			w.Write(",");
			w.Write(TP.Millisecond.ToString("D3"));
			w.Write("),");

			return true;
		}

		private void OutputToFile_Click(object sender, RoutedEventArgs e)
		{
			this.CloseFiles();

			if (this.OutputToFile.IsChecked.HasValue && this.OutputToFile.IsChecked.Value)
			{
				this.eventFirst = true;
				this.eventOutput = File.CreateText(Windows.Storage.ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Events.script");
			}
		}

		private void CloseFiles()
		{
			this.CloseFile(ref this.eventOutput);
		}

		private void CloseFile(ref StreamWriter File)
		{
			StreamWriter w;

			if ((w = File) != null)
			{
				File = null;

				lock (w)
				{
					w.WriteLine("];");
					w.Flush();
					w.Dispose();
				}
			}
		}

	}
}
