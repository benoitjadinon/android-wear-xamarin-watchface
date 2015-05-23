﻿//
//  Copyright 2015 Google Inc. All Rights Reserved.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

using System;
using System.Linq;
using Android.Support.Wearable.Watchface;
using Android.Views;
using Android.OS;
using Android.Graphics;
using Android.App;
using Android.Text.Format;
using Android.Graphics.Drawables;
using Android.Util;
using Java.Util.Concurrent;
using System.Threading;
using Android.Content;
using Android.Service.Wallpaper;
using Android.Provider;
using System.Collections.Generic;
using Android.Support.Wearable.Provider;
using Android.Database;

namespace Google.XamarinSamples.WatchFace
{
	// Sample analog watch face with a ticking second hand. In ambient mode, the second hand isn't shown.
	// On devices with low-bit ambient mode, the hands are drawn without anti-aliasing in ambient mode.
	// The watch face is drawn with less contrast in mute mode.
	// 
	// SweepWatchFaceService is similar but has a sweep second hand.
	[Service (Label="Xamarin Analog Watchface", Permission="android.permission.BIND_WALLPAPER")]
	[MetaData ("android.service.wallpaper", Resource="@xml/watch_face")]
	[MetaData ("com.google.android.wearable.watchface.preview", Resource="@drawable/preview_analog")]
	[IntentFilter (new [] { "android.service.wallpaper.WallpaperService" }, 
	Categories=new [] { "com.google.android.wearable.watchface.category.WATCH_FACE" })]
	public class AnalogWatchFaceService : CanvasWatchFaceService
	{
		const string Tag = "AnalogWatchFaceService";

		/**
		* Update rate in milliseconds for interactive mode. We update once a second to advance the
		* second hand.
		*/
		static long InteractiveUpdateRateMs = TimeUnit.Seconds.ToMillis (1);

		public override WallpaperService.Engine OnCreateEngine ()
		{
			return new AnalogWatchFaceEngine (this);
		}

		private class AnalogWatchFaceEngine : CanvasWatchFaceService.Engine
		{
			const int MsgUpdateTime = 0;

			CanvasWatchFaceService owner;

			Paint hourPaint;
			Paint minutePaint;
			Paint secondPaint;
			Paint tickPaint;
			Paint eventPaint;
			bool mute;
			Time time;

			Event NextEvent {
				get;
				set;
			}

			Timer timerSeconds;
			TimeZoneReceiver timeZoneReceiver;
			bool registeredTimezoneReceiver = false;

			// Whether the display supports fewer bits for each color in ambient mode. When true, we
			// disable anti-aliasing in ambient mode.
			bool lowBitAmbient;

			Bitmap backgroundBitmap;
			Bitmap backgroundScaledBitmap;

			public AnalogWatchFaceEngine (CanvasWatchFaceService owner) : base (owner)
			{
				this.owner = owner;
			}

			public override void OnCreate (ISurfaceHolder surfaceHolder)
			{
				this.SetWatchFaceStyle (new WatchFaceStyle.Builder (this.owner)
					.SetCardPeekMode (WatchFaceStyle.PeekModeShort)
					.SetBackgroundVisibility (WatchFaceStyle.BackgroundVisibilityInterruptive)
					.SetShowSystemUiTime (false)
					.Build ()
				);
				base.OnCreate (surfaceHolder);

				var events = QueryEvents(owner.ApplicationContext.ContentResolver);
				NextEvent = events.FirstOrDefault();

				var backgroundDrawable = owner.Resources.GetDrawable (Resource.Drawable.XamarinWatchFaceBackground);
				backgroundBitmap = (backgroundDrawable as BitmapDrawable).Bitmap;

				hourPaint = new Paint ();
				hourPaint.SetARGB (255, 200, 200, 200);
				hourPaint.StrokeWidth = 5.0f;
				hourPaint.AntiAlias = true;
				hourPaint.StrokeCap = Paint.Cap.Round;

				minutePaint = new Paint ();
				minutePaint.SetARGB (255, 200, 200, 200);
				minutePaint.StrokeWidth = 3.0f;
				minutePaint.AntiAlias = true;
				minutePaint.StrokeCap = Paint.Cap.Round;

				secondPaint = new Paint ();
				secondPaint.SetARGB (255, 255, 0, 0);
				secondPaint.StrokeWidth = 2.0f;
				secondPaint.AntiAlias = true;
				secondPaint.StrokeCap = Paint.Cap.Round;

				tickPaint = new Paint ();
				tickPaint.SetARGB (100, 200, 200, 200);
				tickPaint.StrokeWidth = 2.0f;
				tickPaint.AntiAlias = true;

				eventPaint = new Paint {
					TextSize = 18,
					Color = Color.Blue,
				};
				eventPaint.AntiAlias = true;
				eventPaint.SetStyle(Paint.Style.Fill);

				time = new Time ();
			}

			public override void OnPropertiesChanged (Bundle properties)
			{
				base.OnPropertiesChanged (properties);
				lowBitAmbient = properties.GetBoolean (WatchFaceService.PropertyLowBitAmbient);
				if (Log.IsLoggable (Tag, LogPriority.Debug)) {
					Log.Debug (Tag, "OnPropertiesChanged: low-bit ambient = " + lowBitAmbient);
				}
			}

			public override void OnTimeTick ()
			{
				base.OnTimeTick ();
				if (Log.IsLoggable (Tag, LogPriority.Debug)) {
					Log.Debug (Tag, "onTimeTick: ambient = " + IsInAmbientMode);
				}
				Invalidate ();
			}

			public override void OnAmbientModeChanged (bool inAmbientMode)
			{
				base.OnAmbientModeChanged (inAmbientMode);
				if (Log.IsLoggable (Tag, LogPriority.Debug)) {
					Log.Debug (Tag, "OnAmbientMode");
				}
				if (lowBitAmbient) {
					bool antiAlias = !inAmbientMode;
					hourPaint.AntiAlias = antiAlias;
					minutePaint.AntiAlias = antiAlias;
					secondPaint.AntiAlias = antiAlias;
					tickPaint.AntiAlias = antiAlias;
					eventPaint.AntiAlias = antiAlias;
				}
				Invalidate ();

				UpdateTimer ();
			}

			public override void OnInterruptionFilterChanged (int interruptionFilter)
			{
				base.OnInterruptionFilterChanged (interruptionFilter);
				bool inMuteMode = (interruptionFilter == WatchFaceService.InterruptionFilterNone);
				if (mute != inMuteMode)
				{
					mute = inMuteMode;
					hourPaint.Alpha = inMuteMode ? 100 : 255;
					minutePaint.Alpha = inMuteMode ? 100 : 255;
					secondPaint.Alpha = inMuteMode ? 80 : 255;
					eventPaint.Alpha = inMuteMode ? 100 : 255;
					Invalidate ();
				}
			}

			public override void OnDraw (Canvas canvas, Rect bounds)
			{
				time.SetToNow ();
				int width = bounds.Width ();
				int height = bounds.Height ();

				// Draw the background, scaled to fit.
				if (backgroundScaledBitmap == null
					|| backgroundScaledBitmap.Width != width
					|| backgroundScaledBitmap.Height != height) {
					backgroundScaledBitmap = Bitmap.CreateScaledBitmap (backgroundBitmap,
						width, height, true /* filter */);
				}
				canvas.DrawColor (Color.Black);
				canvas.DrawBitmap (backgroundScaledBitmap, 0, 0, null);

				float centerX = width / 2.0f;
				float centerY = height / 2.0f;

				// Draw the ticks.
				float innerTickRadius = centerX - 10;
				float outerTickRadius = centerX;
				for (int tickIndex = 0; tickIndex < 12; tickIndex++) {
					float tickRot = (float)(tickIndex * Math.PI * 2 / 12);
					float innerX = (float)Math.Sin (tickRot) * innerTickRadius;
					float innerY = (float)-Math.Cos (tickRot) * innerTickRadius;
					float outerX = (float)Math.Sin (tickRot) * outerTickRadius;
					float outerY = (float)-Math.Cos (tickRot) * outerTickRadius;
					canvas.DrawLine (centerX + innerX, centerY + innerY,
						centerX + outerX, centerY + outerY, tickPaint);
				}

				float secRot = time.Second / 30f * (float)Math.PI;
				int minutes = time.Minute;
				float minRot = minutes / 30f * (float)Math.PI;
				float hrRot = ((time.Hour + (minutes / 60f)) / 6f) * (float)Math.PI;

				float secLength = centerX - 20;
				float minLength = centerX - 40;
				float hrLength = centerX - 80;

				if (!IsInAmbientMode) {
					float secX = (float)Math.Sin (secRot) * secLength;
					float secY = (float)-Math.Cos (secRot) * secLength;
					canvas.DrawLine (centerX, centerY, centerX + secX, centerY + secY, secondPaint);
				}

				float minX = (float)Math.Sin (minRot) * minLength;
				float minY = (float)-Math.Cos (minRot) * minLength;
				canvas.DrawLine (centerX, centerY, centerX + minX, centerY + minY, minutePaint);

				float hrX = (float)Math.Sin (hrRot) * hrLength;
				float hrY = (float)-Math.Cos (hrRot) * hrLength;
				canvas.DrawLine (centerX, centerY, centerX + hrX, centerY + hrY, hourPaint);

				// next event
				var next = NextEvent?.Name ?? "no next event";
				canvas.DrawText(next, centerX, centerY, eventPaint);
			}

			public override void OnVisibilityChanged (bool visible)
			{
				base.OnVisibilityChanged (visible);
				if (Log.IsLoggable (Tag, LogPriority.Debug)) {
					Log.Debug (Tag, "OnVisibilityChanged: " + visible);
				}
				if (visible) {
					RegisterTimezoneReceiver ();
					time.Clear (Java.Util.TimeZone.Default.ID);
					time.SetToNow ();
				} else {
					UnregisterTimezoneReceiver ();
				}

				UpdateTimer ();
			}

			private void RegisterTimezoneReceiver ()
			{
				if (registeredTimezoneReceiver) {
					return;
				} else {
					if (timeZoneReceiver == null) {
						timeZoneReceiver = new TimeZoneReceiver ();
						timeZoneReceiver.Receive = (intent) => {
							time.Clear (intent.GetStringExtra ("time-zone"));
							time.SetToNow ();
						};
					}
					registeredTimezoneReceiver = true;
					IntentFilter filter = new IntentFilter (Intent.ActionTimezoneChanged);
					Application.Context.RegisterReceiver (timeZoneReceiver, filter);
				}
			}

			private void UnregisterTimezoneReceiver ()
			{
				if (!registeredTimezoneReceiver) {
					return;
				} else {
					registeredTimezoneReceiver = false;
					Application.Context.UnregisterReceiver (timeZoneReceiver);
				}
			}

			/**
			 * Whether the timer should be running depends on whether we're in ambient mode (as well
			 * as whether we're visible), so we may need to start or stop the timer.
			 */
			private void UpdateTimer ()
			{
				if (Log.IsLoggable (Tag, LogPriority.Debug)) {
					Log.Debug (Tag, "update time");
				}

				if (timerSeconds == null) {
					timerSeconds = new Timer ((state) => {
						Invalidate ();
					}, null, 
						TimeSpan.FromMilliseconds (InteractiveUpdateRateMs), 
						TimeSpan.FromMilliseconds (InteractiveUpdateRateMs));
				} else {
					if (ShouldTimerBeRunning ()) {
						timerSeconds.Change (0, InteractiveUpdateRateMs);
					} else {
						timerSeconds.Change (Timeout.Infinite, 0);
					}
				}
			}

			private bool ShouldTimerBeRunning ()
			{
				return IsVisible && !IsInAmbientMode;
			}




			private static readonly string[] PROJECTION = {
			        CalendarContract.Calendars.InterfaceConsts.Id, // 0
			        CalendarContract.Events.InterfaceConsts.Dtstart, // 1
			        CalendarContract.Events.InterfaceConsts.Dtend, // 2
			        CalendarContract.Events.InterfaceConsts.DisplayColor, // 3
					//CalendarContract.Events.InterfaceConsts.Description, // 4
			};


			protected class Event 
			{
				long Start { get; set; }
				long End { get; set; }
				int Color { get; set; }
				public string Name {
					get { 
						return "Event " + Start;
					}
				}

				public Event(){}
				public Event (long start, long end, int color):this()
				{
					this.Start = start;
					this.End = end;
					this.Color = color;
				}
			}

			//TODO: async
			protected List<Event> QueryEvents(ContentResolver contentResolver) {
			    List<Event> events = new List<Event>();

				long begin = Java.Lang.JavaSystem.CurrentTimeMillis(); //DateTime.Now.Ticks ?

			    var builder = WearableCalendarContract.Instances.ContentUri.BuildUpon();
			    ContentUris.AppendId(builder, begin);
			    ContentUris.AppendId(builder, begin + DateUtils.DayInMillis);

				Android.Net.Uri instURI = builder.Build();
				//TODO: async
				ICursor cursor = contentResolver.Query(instURI,
			                    PROJECTION,
			                    null, // selection (all)
			                    null, // selection args
			                    null); // order

			    while (cursor.MoveToNext()) {
			    	long start = cursor.GetLong(1);
			        long end = cursor.GetLong(2);
			        int color = cursor.GetInt(3);
			        //string desc = cursor.GetString(4);
			        events.Add(new Event(start, end, color));
			    }

			    cursor.Close();

			    return events;
			}
		}

		public class TimeZoneReceiver: BroadcastReceiver
		{
			public Action<Intent> Receive { get; set; }

			public override void OnReceive (Context context, Intent intent)
			{
				if (Receive != null) {
					Receive (intent);
				}
			}
		}

	}

}

