﻿// COPYRIGHT 2010, 2011 by the Open Rails project.
// This code is provided to help you understand what Open Rails does and does
// not do. Suggestions and contributions to improve Open Rails are always
// welcome. Use of the code for any other purpose or distribution of the code
// to anyone else is prohibited without specific written permission from
// admin@openrails.org.
//
// This file is the responsibility of the 3D & Environment Team. 

/* 3D Viewer

    /// This a 3D viewer.  It connects to a simulator engine, rendering the route content and
    /// rolling stock.
    /// 
    /// When the 3D viewer is constructed its passed a reference to the simulator engine, and a flag
    /// indicating if it should operate in fullscreen mode or windowed mode.   After construction, 
    /// LookAt attaches the viewer a TrainCar in the simulator.
    /// 
 *  
 *  The Viewer class actually represents the screen window on which the camera is rendered.
 * 
 * TODO, add note re abandoning Viewer.Components
 *      - control over render order - ie sorting by material to minimize state changes
 *      - multitasking issues
 *      - multipass techniques, such as shadow mapping
 * 
 * 
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MSTS;
using ORTS.Popups;

namespace ORTS {
    public class Viewer3D {
        // User setups.
        public readonly UserSettings Settings;
        public Vector2 WindowSize = new Vector2(1024, 768);
        // Multi-threaded processes
        public UpdaterProcess UpdaterProcess;
        public LoaderProcess LoaderProcess;
        public RenderProcess RenderProcess;
        public SoundProcess SoundProcess;
        // Access to the XNA Game class
        public GraphicsDeviceManager GDM;
        public GraphicsDevice GraphicsDevice;
        public Point DisplaySize;
        // Components
        public Simulator Simulator;
        /// <summary>
        /// Monotonically increasing time value (in seconds) for the game/viewer. Starts at 0 and only ever increases, at real-time.
        /// </summary>
        public double RealTime = 0;
        InfoDisplay InfoDisplay;
        public WindowManager WindowManager = null;
        public SignallingDebugWindow SignallingDebugWindow; // Control-Alt-F11 window
        public MessagesWindow MessagesWindow; // Game message window (special, always visible)
        public PauseWindow PauseWindow; // Game paused window (special)
        public HelpWindow HelpWindow; // F1 window
        public TrackMonitorWindow TrackMonitorWindow; // F4 window
        public HUDWindow HUDWindow; // F5 hud
        public SwitchWindow SwitchWindow; // F8 window
        public TrainOperationsWindow TrainOperationsWindow; // F9 window
        public NextStationWindow NextStationWindow; // F10 window
        public DriverAidWindow DriverAidWindow; // Alt-F11 window
        public CompassWindow CompassWindow; // 0 window
        public ActivityWindow ActivityWindow; // pop-up window
        public SkyDrawer SkyDrawer;
        public PrecipDrawer PrecipDrawer = null;
        //public WireDrawer WireDrawer = null; //commented out as new implementation is in
        public WeatherControl weatherControl;
        TerrainDrawer TerrainDrawer;
        public SceneryDrawer SceneryDrawer;
        public TrainDrawer TrainDrawer;
        public RoadCarHandler RoadCarHandler;
        public SoundSource IngameSounds = null;  // By GeorgeS
        public WorldSounds WorldSounds = null;   // By GeorgeS
        // Route Information
        public Tiles Tiles = null;
        public ENVFile ENVFile;
        public SIGCFGFile SIGCFG;
        public TTypeDatFile TTypeDatFile;
        public bool MilepostUnitsMetric;
        // Cameras
        public Camera Camera; // Current camera
        Camera AboveGroundCamera; // Previous camera for when automatically switching to cab.
        private CabCamera CabCamera; // Camera 1
        private HeadOutCamera HeadOutForwardCamera; // Camera 1+Up
        private HeadOutCamera HeadOutBackCamera; // Camera 2+Down
        private TrackingCamera FrontCamera; // Camera 2
        private TrackingCamera BackCamera; // Camera 3
        private TracksideCamera TracksideCamera; // Camera 4
        private PassengerCamera PassengerCamera; // Camera 5
        private BrakemanCamera BrakemanCamera; // Camera 6
        private List<Camera> WellKnownCameras; // Providing Camera save functionality by GeorgeS
        private int PlayerTrainLength = 0; // re-activate cameras when this changes
        public TrainCarViewer PlayerLocomotiveViewer = null;  // we are controlling this loco, or null if we aren't controlling any
        private MouseState originalMouseState;      // Current mouse coordinates.

        // This is the train we are controlling
        public TrainCar PlayerLocomotive { get { return Simulator.PlayerLocomotive; } set { Simulator.PlayerLocomotive = value; } }
        public Train PlayerTrain { get { if (PlayerLocomotive == null) return null; else return PlayerLocomotive.Train; } }

        // Mouse visibility by timer - GeorgeS
        private bool isMouseShouldVisible = false;
        private bool isMouseTimerVisible = false;
        private double MouseShownAtRealTime = 0;

        public bool SaveActivityThumbnail;
        public string SaveActivityFileStem;

        /// <summary>
        /// Construct a viewer.  At this time background processes are not running
        /// and the graphics device is not ready to accept content.
        /// </summary>
        /// <param name="simulator"></param>
        public Viewer3D(Simulator simulator) {
            Simulator = simulator;
            Settings = simulator.Settings;

            // Parse the screen dimensions.
            var windowSizeParts = Settings.WindowSize.Split(new[] { 'x' }, 2);
            WindowSize.X = Convert.ToInt32(windowSizeParts[0]);
            WindowSize.Y = Convert.ToInt32(windowSizeParts[1]);

            WindowManager = new WindowManager(this);
            SignallingDebugWindow = new SignallingDebugWindow(WindowManager);
            MessagesWindow = new MessagesWindow(WindowManager);
            PauseWindow = new PauseWindow(WindowManager);
            HelpWindow = new HelpWindow(WindowManager);
            TrackMonitorWindow = new TrackMonitorWindow(WindowManager);
            HUDWindow = new HUDWindow(WindowManager);
            SwitchWindow = new SwitchWindow(WindowManager);
            TrainOperationsWindow = new TrainOperationsWindow(WindowManager);
            NextStationWindow = new NextStationWindow(WindowManager);
            DriverAidWindow = new DriverAidWindow(WindowManager);
            CompassWindow = new CompassWindow(WindowManager);
            ActivityWindow = new ActivityWindow(WindowManager);

            WellKnownCameras = new List<Camera>();
            WellKnownCameras.Add(CabCamera = new CabCamera(this));
            WellKnownCameras.Add(FrontCamera = new TrackingCamera(this, TrackingCamera.AttachedTo.Front));
            WellKnownCameras.Add(BackCamera = new TrackingCamera(this, TrackingCamera.AttachedTo.Rear));
            WellKnownCameras.Add(PassengerCamera = new PassengerCamera(this));
            WellKnownCameras.Add(BrakemanCamera = new BrakemanCamera(this));
            WellKnownCameras.Add(HeadOutForwardCamera = new HeadOutCamera(this, HeadOutCamera.HeadDirection.Forward));
            WellKnownCameras.Add(HeadOutBackCamera = new HeadOutCamera(this, HeadOutCamera.HeadDirection.Backward));
            WellKnownCameras.Add(TracksideCamera = new TracksideCamera(this));
        }

        /// <summary>
        /// Save game
        /// </summary>
        public void Save(BinaryWriter outf, string fileStem) {
            outf.Write(Simulator.Trains.IndexOf(PlayerTrain));
            outf.Write(PlayerTrain.Cars.IndexOf(PlayerLocomotive));

            WindowManager.Save(outf);

            outf.Write(WellKnownCameras.IndexOf(Camera));
            foreach (var camera in WellKnownCameras)
                camera.Save(outf);
            Camera.Save(outf);

            // Set these so RenderFrame can use them when its thread gets control.
            SaveActivityFileStem = fileStem;
            SaveActivityThumbnail = true;

            MessagesWindow.AddMessage("Game saved.", 5);
        }

        /// <summary>
        /// Restore after game resumes
        /// </summary>
        public void Restore(BinaryReader inf) {
            Train playerTrain = Simulator.Trains[inf.ReadInt32()];
            PlayerLocomotive = playerTrain.Cars[inf.ReadInt32()];

            WindowManager.Restore(inf);

            var cameraToRestore = inf.ReadInt32();
            foreach (var camera in WellKnownCameras)
                camera.Restore(inf);
            if (cameraToRestore == -1)
                new FreeRoamCamera(this, Camera).Activate();
            else
                WellKnownCameras[cameraToRestore].Activate();
            Camera.Restore(inf);
        }

        [ThreadName("Render")]
        public void Initialize() {
            Materials.ViewingDistance = Settings.ViewingDistance = (int)Math.Min(Simulator.TRK.ORTRKData.MaxViewingDistance, Settings.ViewingDistance);
            if (Settings.SoundDetailLevel > 0) {
                ALSoundSource.MuteAll();  // while loading
                // Swap out original file factory to support loops - by GeorgeS
                // By GeorgeS
                WorldSounds = new WorldSounds(this);
                IngameSounds = new SoundSource(this, Simulator.RoutePath + "\\Sound\\ingame.sms");
            }

            Trace.Write(" ENV");
            ENVFile = new ENVFile(Simulator.RoutePath + @"\ENVFILES\" + Simulator.TRK.Tr_RouteFile.Environment.ENVFileName(Simulator.Season, Simulator.Weather));

            Trace.Write(" SIGCFG");
            SIGCFG = new SIGCFGFile(Simulator.RoutePath + @"\sigcfg.dat");

            Trace.Write(" TTYPE");
            TTypeDatFile = new TTypeDatFile(Simulator.RoutePath + @"\TTYPE.DAT");

            Tiles = new Tiles(Simulator.RoutePath + @"\TILES\");
            MilepostUnitsMetric = Simulator.TRK.Tr_RouteFile.MilepostUnitsMetric;
            SetupBackgroundProcesses();

        }

        [ThreadName("Render")]
        public void Run() {
            RenderProcess.Run();
        }

        /// <summary>
        /// Called once before the graphics device is started to configure the 
        /// graphics card and XNA game engine.
        /// Executes in the RenderProcess thread.
        /// </summary>
        public void Configure(RenderProcess renderProcess) {
            RenderProcess = renderProcess;
            renderProcess.Window.Title = "Open Rails";

            GDM = renderProcess.GraphicsDeviceManager;

            renderProcess.Content.RootDirectory = "Content";

            // TODO, this may cause problems with video cards not set up to handle these settings
            // do we need to check device capabilities first?
            //
            // No. XNA automatically checks capabilities. For example, if the user selects a screen
            // resolution that is greater than what the hardware can support, XNA adjusts the
            // resolution to the actual capability. "...the XNA framework automatically selects the 
            // highest resolution supported by the output device." rvg
            GDM.SynchronizeWithVerticalRetrace = Settings.VerticalSync;
            renderProcess.IsFixedTimeStep = false; // you get smoother animation if we pace to video card retrace setting
            renderProcess.TargetElapsedTime = TimeSpan.FromMilliseconds(1); // setting this a value near refresh rate, ie 16ms, causes hiccups ( beating against refresh rate )
            GDM.PreferredBackBufferWidth = (int)WindowSize.X; // screen.Bounds.Width; // 1680;
            GDM.PreferredBackBufferHeight = (int)WindowSize.Y; // screen.Bounds.Height; // 1050;
            GDM.IsFullScreen = isFullScreen;
            GDM.PreferMultiSampling = true;
            GDM.PreparingDeviceSettings += new EventHandler<PreparingDeviceSettingsEventArgs>(GDM_PreparingDeviceSettings);
        }

        void GDM_PreparingDeviceSettings(object sender, PreparingDeviceSettingsEventArgs e)
        {
            // This enables NVIDIA PerfHud to be run on Open Rails.
            foreach (var adapter in GraphicsAdapter.Adapters)
            {
                if (adapter.Description.Contains("PerfHUD"))
                {
                    e.GraphicsDeviceInformation.Adapter = adapter;
                    e.GraphicsDeviceInformation.DeviceType = DeviceType.Reference;
                    break;
                }
            }

            // This stops ResolveBackBuffer() clearing the back buffer.
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            e.GraphicsDeviceInformation.PresentationParameters.AutoDepthStencilFormat = DepthFormat.Depth24Stencil8;
            UpdateAdapterInformation(e.GraphicsDeviceInformation.Adapter);
        }

        /// <summary>
        /// Called once after the graphics device is ready
        /// to load any static graphics content, background 
        /// processes haven't started yet.
        /// </summary>
        [CallOnThread("Render")]
        public void Initialize(RenderProcess renderProcess) {
            GraphicsDevice = renderProcess.GraphicsDevice;
            DisplaySize.X = GraphicsDevice.Viewport.Width;
            DisplaySize.Y = GraphicsDevice.Viewport.Height;
            if (Settings.ShaderModel == 0)
                Settings.ShaderModel = GraphicsDevice.GraphicsDeviceCapabilities.PixelShaderVersion.Major;
            else if (Settings.ShaderModel < 2)
                Settings.ShaderModel = 2;
            else if (Settings.ShaderModel > 3)
                Settings.ShaderModel = 3;
            if (Settings.ShadowMapDistance == 0)
                Settings.ShadowMapDistance = Settings.ViewingDistance / 2;

            PlayerLocomotive = Simulator.InitialPlayerLocomotive();

            InfoDisplay = new InfoDisplay(this);
            UserInput.Initialize();

            WindowManager.Initialize();

            SkyDrawer = new SkyDrawer(this);
            TerrainDrawer = new TerrainDrawer(this);
            SceneryDrawer = new SceneryDrawer(this);
            if (Settings.Precipitation) PrecipDrawer = new PrecipDrawer(this);
            //if (Settings.Wire) WireDrawer = new WireDrawer(this); //commented out as new implementation is in
            TrainDrawer = new TrainDrawer(this);
            weatherControl = new WeatherControl(this);
            RoadCarHandler = new RoadCarHandler(this);

            PlayerLocomotiveViewer = GetPlayerLocomotiveViewer();

            if (Camera == null)
                FrontCamera.Activate();
            else
                Camera.Activate();

            if (Settings.FullScreen)
                ToggleFullscreen();
        }

        /// <summary>
        /// Called 10 times per second when its safe to read volatile data
        /// from the simulator and viewer classes in preparation
        /// for the Load call.  Copy data to local storage for use 
        /// in the next load call.
        /// </summary>
        [CallOnThread("Updater")]
        public void LoadPrep() {
            TerrainDrawer.LoadPrep();
            SceneryDrawer.LoadPrep();
            TrainDrawer.LoadPrep();
            //if (WireDrawer != null) WireDrawer.LoadPrep(); //commented out as new implementation is in
        }

        /// <summary>
        /// Called 10 times a second to load graphics content
        /// that comes and goes as the player and trains move.
        /// Called from background LoaderProcess Thread
        /// Do not access volatile data from the simulator 
        /// and viewer classes during the Load call ( see
        /// LoadPrep() )
        /// </summary>
        [CallOnThread("Loader")]
        public void Load(RenderProcess renderProcess) {
            TerrainDrawer.Load(renderProcess);
            SceneryDrawer.Load(renderProcess);
            TrainDrawer.Load(renderProcess);
            //if (WireDrawer != null) WireDrawer.Load(renderProcess);//commented out as new implementation is in
        }

        string adapterDescription;
        public string AdapterDescription { get { return adapterDescription; } }

        uint adapterMemory = 0;
        public uint AdapterMemory { get { return adapterMemory; } }

        [CallOnThread("Updater")]
        public void UpdateAdapterInformation(GraphicsAdapter graphicsAdapter) {
            adapterDescription = graphicsAdapter.Description;
            try {
                // Note that we might find multiple adapters with the same
                // description; however, the chance of such adapters not having
                // the same amount of video memory is very slim.
                foreach (ManagementObject videoController in new ManagementClass("Win32_VideoController").GetInstances())
                    if (((string)videoController["Description"] == adapterDescription) && (videoController["AdapterRAM"] != null))
                        adapterMemory = (uint)videoController["AdapterRAM"];
            } catch (ManagementException error) {
                Trace.WriteLine(error);
            }
        }

        /// <summary>
        /// Called whenever a key or mouse buttin is pressed for handling user input
        /// elapsedTime represents the the time since the last call to HandleUserInput
        /// Examine the static class UserInput for mouse and keyboard status
        /// </summary>
        [CallOnThread("Updater")]
        public void HandleUserInput(ElapsedTime elapsedTime) {
            Camera.HandleUserInput(elapsedTime);

            if (PlayerLocomotiveViewer != null)
                PlayerLocomotiveViewer.HandleUserInput(elapsedTime);

            InfoDisplay.HandleUserInput(elapsedTime);
            WindowManager.HandleUserInput(elapsedTime);

            // Check for game control keys
            if (UserInput.IsPressed(UserCommands.GameQuit)) { Stop(); return; }
            if (UserInput.IsPressed(UserCommands.GameFullscreen)) { ToggleFullscreen(); }
            if (UserInput.IsPressed(UserCommands.GamePause)) Simulator.Paused = !Simulator.Paused;
            if (UserInput.IsPressed(UserCommands.DebugSpeedUp)) Simulator.GameSpeed *= 1.5f;
            if (UserInput.IsPressed(UserCommands.DebugSpeedDown)) Simulator.GameSpeed /= 1.5f;
            if (UserInput.IsPressed(UserCommands.DebugSpeedReset)) Simulator.GameSpeed = 1;
            if (UserInput.IsPressed(UserCommands.GameSave)) { Program.Save(); }
            if (UserInput.IsPressed(UserCommands.DisplayHelpWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) HelpWindow.TabAction(); else HelpWindow.Visible = !HelpWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplayTrackMonitorWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) TrackMonitorWindow.TabAction(); else TrackMonitorWindow.Visible = !TrackMonitorWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplayHUD)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) HUDWindow.TabAction(); else HUDWindow.Visible = !HUDWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplaySwitchWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) SwitchWindow.TabAction(); else SwitchWindow.Visible = !SwitchWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplayTrainOperationsWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) TrainOperationsWindow.TabAction(); else TrainOperationsWindow.Visible = !TrainOperationsWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplayNextStationWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) NextStationWindow.TabAction(); else NextStationWindow.Visible = !NextStationWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DisplayCompassWindow)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) CompassWindow.TabAction(); else CompassWindow.Visible = !CompassWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DebugDriverAid)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) DriverAidWindow.TabAction(); else DriverAidWindow.Visible = !DriverAidWindow.Visible;
            if (UserInput.IsPressed(UserCommands.DebugSignalling)) if (UserInput.IsDown(UserCommands.DisplayNextWindowTab)) SignallingDebugWindow.TabAction(); else SignallingDebugWindow.Visible = !SignallingDebugWindow.Visible;

            if (UserInput.IsPressed(UserCommands.GameLocomotiveSwitch)) {
                Simulator.PlayerLocomotive.Train.LeadNextLocomotive();
                Simulator.PlayerLocomotive = Simulator.PlayerLocomotive.Train.LeadLocomotive;
                Simulator.PlayerLocomotive.Train.CalculatePositionOfCars(0);  // fix the front traveller
                Simulator.PlayerLocomotive.Train.RepositionRearTraveller();    // fix the rear traveller
                PlayerLocomotiveViewer = TrainDrawer.GetViewer(Simulator.PlayerLocomotive);
                PlayerTrainLength = 0;
            }

            if (UserInput.IsPressed(UserCommands.CameraCab) && CabCamera.IsAvailable) CabCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraOutsideFront)) FrontCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraOutsideRear)) BackCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraTrackside)) TracksideCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraPassenger) && PassengerCamera.IsAvailable) PassengerCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraBrakeman)) BrakemanCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraFree)) new FreeRoamCamera(this, Camera).Activate();
            if (UserInput.IsPressed(UserCommands.CameraHeadOutForward) && HeadOutForwardCamera.IsAvailable) HeadOutForwardCamera.Activate();
            if (UserInput.IsPressed(UserCommands.CameraHeadOutBackward) && HeadOutBackCamera.IsAvailable) HeadOutBackCamera.Activate();

            if (UserInput.IsPressed(UserCommands.GameSwitchAhead)) Simulator.SwitchTrackAhead(PlayerTrain);
            if (UserInput.IsPressed(UserCommands.GameSwitchBehind)) Simulator.SwitchTrackBehind(PlayerTrain);
            if (UserInput.IsPressed(UserCommands.DebugLocomotiveFlip)) { Simulator.PlayerLocomotive.Flipped = !Simulator.PlayerLocomotive.Flipped; Simulator.PlayerLocomotive.SpeedMpS *= -1; }
            if (UserInput.IsPressed(UserCommands.DebugResetSignal)) PlayerTrain.ResetSignal(true);
            if (!Simulator.Paused && UserInput.IsDown(UserCommands.GameSwitchWithMouse)) {
                isMouseShouldVisible = true;
                if (UserInput.MouseState.LeftButton == ButtonState.Pressed && UserInput.Changed) {
                    TryThrowSwitchAt();
                    UserInput.Handled();
                }
            } else if (!Simulator.Paused && UserInput.IsDown(UserCommands.GameUncoupleWithMouse)) {
                isMouseShouldVisible = true;
                if (UserInput.MouseState.LeftButton == ButtonState.Pressed && UserInput.Changed) {
                    TryUncoupleAt();
                    UserInput.Handled();
                }
            } else {
                isMouseShouldVisible = false;
            }

            if (PlayerTrainLength != PlayerTrain.Cars.Count)
            {
                PlayerTrainLength = PlayerTrain.Cars.Count;
                if (!Camera.IsAvailable)
                    FrontCamera.Activate();
                else
                    Camera.Activate();
            }

            RenderProcess.IsMouseVisible = isMouseShouldVisible || isMouseTimerVisible;

            if (UserInput.RDState != null)
                UserInput.RDState.Handled();
        }


        //
        //  This is to enable the user to move popup windows
        //  Coded as a separate routine as HandleUserInput does not cater for mouse movemenmt.
        //
        [CallOnThread("Updater")]
        public void HandleMouseMovement() {
            MouseState currentMouseState = Mouse.GetState();

            // Handling mouse movement and timing - GeorgeS
            if (currentMouseState.X != originalMouseState.X ||
                currentMouseState.Y != originalMouseState.Y) {
                isMouseTimerVisible = true;
                MouseShownAtRealTime = RealTime;
                RenderProcess.IsMouseVisible = isMouseShouldVisible || isMouseTimerVisible;
            } else if (isMouseTimerVisible && MouseShownAtRealTime + .5 < RealTime) {
                isMouseTimerVisible = false;
                RenderProcess.IsMouseVisible = isMouseShouldVisible || isMouseTimerVisible;
            }

            originalMouseState = currentMouseState;
        }

        /// <summary>
        /// Called every frame to update animations and load the frame contents .
        /// Note:  this doesn't actually draw on the screen surface, but 
        /// instead prepares a list of drawing primitives that will be rendered
        /// later in RenderFrame.Draw() by the RenderProcess thread.
        /// elapsedTime represents the the time since the last call to PrepareFrame.
        /// </summary>
        [CallOnThread("Updater")]
        public void PrepareFrame(RenderFrame frame, ElapsedTime elapsedTime) {
            // Mute sound when paused
            if (Simulator.Paused)
                ALSoundSource.MuteAll();
            else
                ALSoundSource.UnMuteAll();

            if (ScreenHasChanged()) {
                Camera.ScreenChanged();
                RenderProcess.InitializeShadowMapLocations(RenderProcess.Viewer);
            }

            // Update camera first...
            Camera.Update(elapsedTime);
            // No above camera means we're allowed to auto-switch to cab view.
            if ((AboveGroundCamera == null) && Camera.IsUnderground) {
                AboveGroundCamera = Camera;
                CabCamera.Activate();
            } else if (AboveGroundCamera != null) {
                // Make sure to keep the old camera updated...
                AboveGroundCamera.Update(elapsedTime);
                // ...so we can tell when to come back to it.
                if (!AboveGroundCamera.IsUnderground) {
                    // But only if the user hasn't selected another camera!
                    if (Camera == CabCamera)
                        AboveGroundCamera.Activate();
                    AboveGroundCamera = null;
                }
            }
            // We're now ready to prepare frame for the camera.
            Camera.PrepareFrame(frame, elapsedTime);

            frame.PrepareFrame(elapsedTime);
            SkyDrawer.PrepareFrame(frame, elapsedTime);
            TerrainDrawer.PrepareFrame(frame, elapsedTime);
            SceneryDrawer.PrepareFrame(frame, elapsedTime);
            TrainDrawer.PrepareFrame(frame, elapsedTime);
            RoadCarHandler.PrepareFrame(frame, elapsedTime);
            if (PrecipDrawer != null) PrecipDrawer.PrepareFrame(frame, elapsedTime);
            InfoDisplay.PrepareFrame(frame, elapsedTime);
            
            if (this.Simulator.ActivityRun != null) { ActivityWindow.PrepareFrame(elapsedTime, true); }
            
            WindowManager.PrepareFrame(frame, elapsedTime);
        }


        /// <summary>
        /// Unload all graphical content and restore memory
        /// </summary>
        [CallOnThread("Render")]
        public void Unload(RenderProcess renderProcess) {
            SoundProcess.RemoveAllSources();
        }

        public void Stop() {
            InfoDisplay.Stop();
            RenderProcess.Stop();
        }

        /// <summary>
        /// Report an Exception from a background process (e.g. loader).
        /// </summary>
        /// <param name="error"></param>
        public void ProcessReportError(Exception error) {
            // Log the error first in case we're burning.
            Trace.WriteLine(error);
            // Stop the world!
            Stop();
            // Show the user that it's all gone horribly wrong.
            if (Settings.ShowErrorDialogs)
                System.Windows.Forms.MessageBox.Show(error.ToString());
        }

        /// <summary>
        /// Adjust all projection matrixes and buffer sizes
        /// </summary>
        private void ToggleFullscreen() {
            bool IsFullScreen = !GDM.IsFullScreen;
            if (IsFullScreen) {
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.PrimaryScreen;
                GDM.PreferredBackBufferWidth = screen.Bounds.Width;
                GDM.PreferredBackBufferHeight = screen.Bounds.Height;
                GDM.PreferredBackBufferFormat = SurfaceFormat.Color;
                GDM.PreferredDepthStencilFormat = DepthFormat.Depth32;
            } else {
                GDM.PreferredBackBufferWidth = (int)WindowSize.X;
                GDM.PreferredBackBufferHeight = (int)WindowSize.Y;
            }
            RenderProcess.ToggleFullScreen();
        }

        /// <summary>
        /// Return true if the screen has changed dimensions
        /// </summary>
        /// <returns></returns>
        private bool ScreenHasChanged() {
            if (RenderProcess.GraphicsDeviceManager.IsFullScreen != isFullScreen) {
                isFullScreen = RenderProcess.GraphicsDeviceManager.IsFullScreen;
                return true;
            }
            return false;
        }
        private bool isFullScreen = false;

        private TrainCarViewer GetPlayerLocomotiveViewer() {
            return TrainDrawer.GetViewer(PlayerLocomotive);
        }

        /// <summary>
        /// The user has left clicked with U pressed.   
        /// If the mouse was over a coupler, then uncouple the car.
        /// </summary>
        private void TryUncoupleAt() {
            // Create a ray from the near clip plane to the far clip plane.
            Vector3 direction = UserInput.FarPoint - UserInput.NearPoint;
            direction.Normalize();
            Ray pickRay = new Ray(UserInput.NearPoint, direction);

            // check each car
            TDBTraveller traveller = new TDBTraveller(PlayerTrain.FrontTDBTraveller);
            traveller.ReverseDirection();
            foreach (TrainCar car in PlayerTrain.Cars) {
                float d = (car.CouplerSlackM + car.GetCouplerZeroLengthM()) / 2;
                traveller.Move(car.Length + d);

                Vector3 xnaCenter = Camera.XNALocation(traveller.WorldLocation);
                float radius = 2f;  // 2 meter click range
                BoundingSphere boundingSphere = new BoundingSphere(xnaCenter, radius);

                if (null != pickRay.Intersects(boundingSphere)) {
                    Simulator.UncoupleBehind(car);
                    break;
                }
                traveller.Move(d);
            }
        }
        /// <summary>
        /// The user has left clicked with U pressed.   
        /// If the mouse was over a coupler, then uncouple the car.
        /// </summary>
        private void TryThrowSwitchAt() {
            TrJunctionNode bestNode = null;
            float bestD = 10;
            // check each switch
            for (int j = 0; j < Simulator.TDB.TrackDB.TrackNodes.Count(); j++) {
                TrackNode tn = Simulator.TDB.TrackDB.TrackNodes[j];
                if (tn != null && tn.TrJunctionNode != null) {

                    Vector3 xnaCenter = Camera.XNALocation(new WorldLocation(tn.UiD.TileX, tn.UiD.TileZ, tn.UiD.X, tn.UiD.Y, tn.UiD.Z));
                    float d = ORTSMath.LineSegmentDistanceSq(xnaCenter, UserInput.NearPoint, UserInput.FarPoint);
                    if (bestD > d && !Simulator.SwitchIsOccupied(j)) {
                        bestNode = tn.TrJunctionNode;
                        bestD = d;
                    }
                }
            }
            if (bestNode != null)
                bestNode.SelectedRoute = 1 - bestNode.SelectedRoute;
        }

        public void SetupBackgroundProcesses() {
            RenderProcess = new RenderProcess(this);   // the order is important, since one process depends on the next
            LoaderProcess = new LoaderProcess(this);
            UpdaterProcess = new UpdaterProcess(this);
            SoundProcess = new SoundProcess(this);
        }


    } // Viewer3D
} // namespace ORTS
