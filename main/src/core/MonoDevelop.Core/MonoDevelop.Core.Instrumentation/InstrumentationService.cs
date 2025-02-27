// 
// InstrumentationService.cs
//  
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
// 
// Copyright (c) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#nullable enable

using System;
using System.IO;
using System.Collections.Generic;
using MonoDevelop.Core.ProgressMonitoring;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Threading;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using MonoDevelop.Core.Execution;
using Mono.Addins;
using Newtonsoft.Json;

namespace MonoDevelop.Core.Instrumentation
{
	public static class InstrumentationService
	{
		static readonly Dictionary <string, Counter> counters = new Dictionary<string, Counter> ();
		static readonly Dictionary <string, Counter> countersByID = new Dictionary<string, Counter> ();
		static readonly List<CounterCategory> categories = new List<CounterCategory> ();
		static readonly List<InstrumentationConsumer> handlers = new List<InstrumentationConsumer> ();
		static bool enabled = true;
		static DateTime startTime = DateTime.Now;
		static int publicPort = -1;
		static Thread? autoSaveThread;
		static bool stopping;
		static int autoSaveInterval;
		static bool handlersLoaded;
		
		internal static void InitializeHandlers ()
		{
			if (!handlersLoaded && AddinManager.IsInitialized) {
				lock (counters) {
					handlersLoaded = true;
					AddinManager.AddExtensionNodeHandler (typeof(InstrumentationConsumer), HandleInstrumentationHandlerExtension);
				}
			}
		}
		
		static void UpdateCounterStatus ()
		{
			lock (counters) {
				foreach (var c in counters.Values)
					c.UpdateStatus ();
			}
		}
		
		static void HandleInstrumentationHandlerExtension (object sender, ExtensionNodeEventArgs args)
		{
			var handler = (InstrumentationConsumer)args.ExtensionObject;
			if (args.Change == ExtensionChange.Add) {
				RegisterInstrumentationConsumer (handler);
			}
			else {
				UnregisterInstrumentationConsumer (handler);
			}
		}

		public static void RegisterInstrumentationConsumer (InstrumentationConsumer consumer)
		{
			lock (counters) {
				handlers.Add (consumer);
				foreach (var c in counters.Values) {
					if (consumer.SupportsCounter (c))
						c.Handlers.Add (consumer);
				}
			}
			UpdateCounterStatus ();
		}
		
		public static void UnregisterInstrumentationConsumer (InstrumentationConsumer consumer)
		{
			lock (counters) {
				handlers.Remove (consumer);
				foreach (var c in counters.Values)
					c.Handlers.Remove (consumer);
			}
			UpdateCounterStatus ();
		}

		public static int PublishService ()
		{
			RemotingService.RegisterRemotingChannel ();
			TcpChannel ch = (TcpChannel) ChannelServices.GetChannel ("tcp");
			Uri u = new Uri (ch.GetUrlsForUri ("test")[0]);
			publicPort = u.Port;
			
			InstrumentationServiceBackend backend = new InstrumentationServiceBackend ();
			System.Runtime.Remoting.RemotingServices.Marshal (backend, "InstrumentationService");
			
			return publicPort;
		}
		
		public static void StartMonitor ()
		{
			if (publicPort == -1)
				throw new InvalidOperationException ("Service not published");
			
			if (Platform.IsMac) {
				var macOSDir = PropertyService.EntryAssemblyPath.ParentDirectory.ParentDirectory.ParentDirectory.ParentDirectory.Combine ("MacOS");
				var app = macOSDir.Combine ("MDMonitor.app");
				if (Directory.Exists (app)) {
					var psi = new ProcessStartInfo ("open", string.Format ("-n '{0}' --args -c localhost:{1} ", app, publicPort)) {
						UseShellExecute = false,
					};
					Process.Start (psi);
					return;
				}
			}	
			
			string exe = Path.Combine (Path.GetDirectoryName (Assembly.GetEntryAssembly ().Location), "mdmonitor.exe");
			string args = "-c localhost:" + publicPort;
			Runtime.SystemAssemblyService.CurrentRuntime.ExecuteAssembly (exe, args);
		}
		
		public static void StartAutoSave (string file, int interval)
		{
			autoSaveInterval = interval;
			autoSaveThread = new Thread (delegate () {
				AutoSave (file, interval);
			});
			autoSaveThread.IsBackground = true;
			autoSaveThread.Start ();
		}
		
		public static void Stop ()
		{
			stopping = true;
			if (autoSaveThread != null)
				autoSaveThread.Join (autoSaveInterval*3);
		}
		
		static void AutoSave (string file, int interval)
		{
			while (!stopping) {
				Thread.Sleep (interval);
				lock (counters) {
					Save (file, (fs, data) => new BinaryFormatter ().Serialize (fs, data));
				}
			}
			autoSaveThread = null;
		}

		internal static void SaveJson (string filePath)
		{
			Save (filePath, (fs, data) => {
				using var writer = new StreamWriter (fs);
				var serializer = JsonSerializer.Create (new JsonSerializerSettings {
					ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
					DefaultValueHandling = DefaultValueHandling.Ignore,
					NullValueHandling = NullValueHandling.Ignore,
				});
				serializer.Serialize (writer, data);
			});
		}

		static void Save (string filePath, Action<Stream, IInstrumentationService> serializer)
		{
			try {
				FilePath tempPath = filePath + ".tmp";
				using (Stream fs = File.OpenWrite (tempPath)) {
					serializer (fs, GetServiceData ());
				}
				FileService.SystemRename (tempPath, filePath);
			} catch (Exception ex) {
				LoggingService.LogError ("Instrumentation service data could not be saved", ex);
			}
		}
		
		public static IInstrumentationService GetRemoteService (string hostAndPort)
		{
			return (IInstrumentationService) Activator.GetObject (typeof(IInstrumentationService), "tcp://" + hostAndPort + "/InstrumentationService");
		}

		public static IInstrumentationService GetServiceData ()
		{
			return new InstrumentationServiceData (counters, categories) {
				EndTime = DateTime.Now,
				StartTime = StartTime,
			};
		}
		
		public static IInstrumentationService LoadServiceDataFromFile (string file)
		{
			using (Stream s = File.OpenRead (file)) {
				var f = new BinaryFormatter ();
				var data = f.Deserialize (s) as IInstrumentationService;
				if (data == null)
					throw new Exception ("Invalid instrumentation service data file");
				return data;
			}
		}
		
		public static bool Enabled {
			get { return enabled; }
			set {
				if (enabled == value)
					return;
				enabled = value;
				UpdateCounterStatus ();
			}
		}
		
		public static DateTime StartTime {
			get { return startTime; }
		}
		
		public static Counter CreateCounter (string name)
		{
			return CreateCounter (name, null);
		}
		
		public static Counter CreateCounter (string name, string? category)
		{
			return CreateCounter (name, category, false);
		}
		
		public static Counter CreateCounter (string name, string? category, bool logMessages)
		{
			return CreateCounter (name, category, logMessages, null, false);
		}
		
		public static Counter CreateCounter (string name, string? category = null, bool logMessages = false, string? id = null)
		{
			return CreateCounter (name, category, logMessages, id, false);
		}

		public static Counter<T> CreateCounter<T> (string name, string? category = null, bool logMessages = false, string? id = null) where T : CounterMetadata, new()
		{
			return (Counter<T>) CreateCounter<T> (name, category, logMessages, id, false);
		}

		static Counter CreateCounter (string name, string? category, bool logMessages, string? id, bool isTimer)
		{
			return CreateCounter<CounterMetadata> (name, category, logMessages, id, isTimer);
		}

		static Counter CreateCounter<T> (string name, string? category, bool logMessages, string? id, bool isTimer) where T:CounterMetadata, new()
		{
			if (name == null)
				throw new ArgumentNullException ("name", "Counters must have a Name");

			InitializeHandlers ();
			
			if (category == null)
				category = "Global";
				
			lock (counters) {
				CounterCategory? cat = GetCategory (category);
				if (cat == null) {
					cat = new CounterCategory (category);
					categories.Add (cat);
				}
				
				var c = isTimer ? new TimerCounter<T> (name, cat) : (Counter) new Counter<T> (name, cat);
				c.Id = id!;
				c.LogMessages = logMessages;
				cat.AddCounter (c);
				
				Counter old;
				if (counters.TryGetValue (name, out old))
					old.Disposed = true;
				counters [name] = c;
				if (!string.IsNullOrEmpty (id)) {
					countersByID [id!] = c;
				}

				foreach (var h in handlers) {
					if (h.SupportsCounter (c))
						c.Handlers.Add (h);
				}
				c.UpdateStatus ();
				
				return c;
			}
		}
		
		public static MemoryProbe? CreateMemoryProbe (string name)
		{
			return CreateMemoryProbe (name, null);
		}
		
		public static MemoryProbe? CreateMemoryProbe (string name, string? category)
		{
			if (!enabled)
				return null;
			
			Counter c;
			lock (counters) {
				if (!counters.TryGetValue (name, out c))
					c = CreateCounter (name, category);
			}
			return new MemoryProbe (c);
		}
		
		public static TimerCounter CreateTimerCounter (string name)
		{
			return CreateTimerCounter (name, null);
		}
		
		public static TimerCounter CreateTimerCounter (string name, string? category)
		{
			return CreateTimerCounter (name, category, 0, false);
		}
		

		public static TimerCounter CreateTimerCounter (string name, string? category = null, double minSeconds = 0, bool logMessages = false, string? id = null)
		{
			TimerCounter c = (TimerCounter) CreateCounter (name, category, logMessages, id, true);
			c.LogMessages = logMessages;
			c.MinSeconds = minSeconds;
			return c;
		}
		
		public static TimerCounter<T> CreateTimerCounter<T> (string name, string? category = null, double minSeconds = 0, bool logMessages = false, string? id = null) where T:CounterMetadata, new()
		{
			var c = (TimerCounter<T>) CreateCounter<T> (name, category, logMessages, id, true);
			c.LogMessages = logMessages;
			c.MinSeconds = minSeconds;
			return c;
		}
		
		public static IEnumerable<Counter> GetCounters ()
		{
			lock (counters) {
				return new List<Counter> (counters.Values);
			}
		}
		
		public static Counter GetCounter (string name)
		{
			lock (counters) {
				if (counters.TryGetValue (name, out var c))
					return c;
				return counters[name] = new Counter (name, null);
			}
		}

		public static Counter GetCounterByID (string id)
		{
			lock (counters) {
				return countersByID [id];
			}
		}

		public static CounterCategory? GetCategory (string name)
		{
			lock (counters) {
				foreach (CounterCategory cat in categories)
					if (cat.Name == name)
						return cat;
				return null;
			}
		}
		
		public static IEnumerable<CounterCategory> GetCategories ()
		{
			lock (counters) {
				return new List<CounterCategory> (categories);
			}
		}

		[ThreadStatic]
		internal static bool IsLoggingMessage;
		
		internal static void LogMessage (string message)
		{
			IsLoggingMessage = true;
			try {
				LoggingService.LogInfo (message);
			} finally {
				IsLoggingMessage = false;
			}
		}
		
		public static void Dump ()
		{
			foreach (CounterCategory cat in categories) {
				Console.WriteLine (cat.Name);
				Console.WriteLine (new string ('-', cat.Name.Length));
				Console.WriteLine ();
				foreach (Counter c in cat.Counters)
					Console.WriteLine ("{0,-6} {1,-6} : {2}", c.Count, c.TotalCount, c.Name);
				Console.WriteLine ();
			}
		}
		
		public static ProgressMonitor GetInstrumentedMonitor (ProgressMonitor monitor, TimerCounter counter)
		{
			if (enabled) {
				AggregatedProgressMonitor mon = new AggregatedProgressMonitor (monitor);
				mon.AddFollowerMonitor (new IntrumentationMonitor (counter), MonitorAction.Tasks | MonitorAction.WriteLog);
				return mon;
			} else
				return monitor;
		}
	}
	
	class IntrumentationMonitor: ProgressMonitor
	{
		TimerCounter counter;
		Stack<ITimeTracker?> timers = new Stack<ITimeTracker?> ();

		public IntrumentationMonitor (TimerCounter counter)
		{
			this.counter = counter;
		}

		protected override void OnWriteLog (string message)
		{
			if (timers.Count > 0)
				timers.Peek ()?.Trace (message);
		}

		protected override void OnBeginTask (string name, int totalWork, int stepWork)
		{
			if (!string.IsNullOrEmpty (name)) {
				ITimeTracker c = counter.BeginTiming (name);
				c.Trace (name);
				timers.Push (c);
			} else {
				timers.Push (null);
			}
		}

		protected override void OnEndTask (string name, int totalWork, int stepWork)
		{
			if (timers.Count > 0) {
				ITimeTracker? c = timers.Pop ();
				if (c != null)
					c.End ();
			}
		}
	}
	
	public interface IInstrumentationService
	{
		DateTime StartTime { get; }
		DateTime EndTime { get; }
		IEnumerable<Counter> GetCounters ();
		Counter GetCounter (string name);
		CounterCategory? GetCategory (string name);
		IEnumerable<CounterCategory> GetCategories ();
	}
	
	class InstrumentationServiceBackend: MarshalByRefObject, IInstrumentationService
	{
		public DateTime StartTime {
			get {
				return InstrumentationService.StartTime;
			}
		}
		
		public DateTime EndTime {
			get {
				return DateTime.Now;
			}
		}

		public IEnumerable<Counter> GetCounters ()
		{
			return InstrumentationService.GetCounters ();
		}
		
		public Counter GetCounter (string name)
		{
			return InstrumentationService.GetCounter (name);
		}
		
		public CounterCategory? GetCategory (string name)
		{
			return InstrumentationService.GetCategory (name);
		}
		
		public IEnumerable<CounterCategory> GetCategories ()
		{
			return InstrumentationService.GetCategories ();
		}
		
		public override object? InitializeLifetimeService ()
		{
			return null;
		}
	}
	
	[Serializable]
	class InstrumentationServiceData: IInstrumentationService
	{
		public DateTime StartTime { get; set; }
		public DateTime EndTime { get; set; }
		public Dictionary <string, Counter> Counters { get; }
		public List<CounterCategory> Categories { get; }

		public InstrumentationServiceData (Dictionary<string, Counter> counters, List<CounterCategory> categories)
		{
			Counters = counters;
			Categories = categories;
		}
		
		public IEnumerable<Counter> GetCounters ()
		{
			return Counters.Values;
		}
		
		public Counter GetCounter (string name)
		{
			if (Counters.TryGetValue (name, out Counter c))
				return c;
			return Counters [name] = new Counter (name, null);
		}
		
		public CounterCategory GetCategory (string name)
		{
			return Categories.FirstOrDefault (c => c.Name == name);
		}
		
		public IEnumerable<CounterCategory> GetCategories ()
		{
			return Categories;
		}
	}
}
