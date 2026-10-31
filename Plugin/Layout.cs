using System;
using Il2CppInterop.Runtime;

namespace CrapMechanics
{
    /// <summary>Offsets and sizes of the game's structs that the mod reads and writes raw. They are read from the game when the mod
    /// loads (il2cpp field info, by name), so a game update that moves fields doesn't make the mod read the wrong bytes: the 10-31
    /// update inserted four fields in GarageGrabberSingleton and the old fixed +216 crashed the game on G. A field the game no longer
    /// has makes Init fail and the mod stays off (with the field's name in the log) instead of corrupting memory.</summary>
    internal static class Layout
    {
        // SpaceshipElectricPort
        public static int PortPersist, PortValue, PortEnd, PortSetup, PortRuntime, PortIndex;
        // SCElectricPower
        public static int PowerStored, PowerGen, PowerCons, PowerState, PowerCut, PowerSize;
        // Core.Singleton (availability maps), GarageGrabberSingleton
        public static int CoreStandalone, CoreMax, GarOver, GarRemaining;
        // FuelData, SCTypeCableCell, SCTypeWirelessTransmitter, Netcore.Singleton
        public static int FuelCurrent, CellType, CellMaxPower, CellFwd, CellBwd, TransmIndicator, NetServer, NetStatus, ClientSteam;
        // SCTick_Radar.Singleton and its RadarLocationGPUData buffer element
        public static int RadarView, RadarProj, RadarAny, GpuUv, GpuType, GpuTarget, GpuSize;
        // SeatBindingsUpdateSystem: Singleton -> SeatBindings._actionables (the list), BindingActionable (its element)
        public static int SeatList, BindTarget, BindDistance, BindSize;
        // CRPRendererData, CRPLocalBounds, SCPaintableColor
        public static int RendDraw0, BoundsAabb, PaintPainted, PaintDefault;
        // SCTypeCamera, SCTypeMonitor, PlayerControllerData
        public static int CamEntity, CamNextDraw, CamSlice, MonSignals, MonSlices, PlayerCamera;

        /// <summary>Reads every offset; false (with the reason) when the game no longer has one of the fields.</summary>
        public static bool Init(out string error)
        {
            try
            {
                var p = typeof(SpaceshipElectricPort);
                PortPersist = Off(p, "_persistentConnectedCCOrPort"); PortValue = Off(p, "_portValue"); PortEnd = Off(p, "_runtimeEndEntity");
                PortSetup = Off(p, "_setupType"); PortRuntime = Off(p, "_runtimeType"); PortIndex = Off(p, "_index");
                var w = typeof(SCElectricPower);
                PowerStored = Off(w, "_runtimePower"); PowerGen = Off(w, "_generatePerSec"); PowerCons = Off(w, "_consumptionPerSec");
                PowerState = Off(w, "_suppliedState"); PowerCut = Off(w, "_isElectricityInterrupted"); PowerSize = Size(w);
                CoreStandalone = Off(typeof(Core.Singleton), "_standaloneComponentsAvailability"); CoreMax = Off(typeof(Core.Singleton), "_maxComponentsAvailability");
                GarOver = Off(typeof(GarageGrabberSingleton), "_isComponentsLimitOverflow"); GarRemaining = Off(typeof(GarageGrabberSingleton), "_remainingComponentsMapPtr");
                FuelCurrent = Off(typeof(FuelData), "_currentFuel");
                CellType = Off(typeof(SCTypeCableCell), "_type"); CellMaxPower = Off(typeof(SCTypeCableCell), "_maxPower"); CellFwd = Off(typeof(SCTypeCableCell), "_connectedFwd"); CellBwd = Off(typeof(SCTypeCableCell), "_connectedBwd");
                TransmIndicator = Off(typeof(SCTypeWirelessTransmitter), "_indicatorEntity");
                NetServer = Off(typeof(Netcore.Singleton), "_isServer"); NetStatus = Off(typeof(Netcore.Singleton), "_networkStatus"); ClientSteam = Off(typeof(NetcoreClient), "_steamID");
                var r = typeof(SCTick_Radar.Singleton);
                RadarView = Off(r, "_viewMatrix"); RadarProj = Off(r, "_projectionMatrix"); RadarAny = Off(r, "_anyActiveRadar");
                var g = typeof(SCTick_Radar.RadarLocationGPUData);
                GpuUv = Off(g, "_uv"); GpuType = Off(g, "_type"); GpuTarget = Off(g, "_isTarget"); GpuSize = Size(g);
                SeatList = Off(typeof(SeatBindingsUpdateSystem.Singleton), "_currentSeatBindings") + Off(typeof(SeatBindingsUpdateSystem.SeatBindings), "_actionables");
                var b = typeof(SeatBindingsUpdateSystem.BindingActionable);
                BindTarget = Off(b, "_targetActionable"); BindDistance = Off(b, "_distance"); BindSize = Size(b);
                RendDraw0 = Off(typeof(CRPRendererData), "_drawData0"); BoundsAabb = Off(typeof(CRPLocalBounds), "_localBounds");
                PaintPainted = Off(typeof(SCPaintableColor), "_painted"); PaintDefault = Off(typeof(SCPaintableColor), "_defaultColor");
                CamEntity = Off(typeof(SCTypeCamera), "_cameraEntity"); CamNextDraw = Off(typeof(SCTypeCamera), "_nextDrawTime"); CamSlice = Off(typeof(SCTypeCamera), "_renderingSlice");
                MonSignals = Off(typeof(SCTypeMonitor), "_lastReceivedSignals"); MonSlices = Off(typeof(SCTypeMonitor), "_assignedRenderingSlices");
                PlayerCamera = Off(typeof(PlayerControllerData), "_cameraEntity");
                error = null;
                ModLog.Info("layout: port value/end/setup/runtime/index=" + PortValue + "/" + PortEnd + "/" + PortSetup + "/" + PortRuntime + "/" + PortIndex +
                            ", power gen/cons/state/size=" + PowerGen + "/" + PowerCons + "/" + PowerState + "/" + PowerSize + ", core maps=" + CoreStandalone + "/" + CoreMax +
                            ", garage=" + GarOver + "/" + GarRemaining + ", fuel=" + FuelCurrent + ", cell=" + CellMaxPower + "/" + CellFwd + "/" + CellBwd + ", transmitter=" + TransmIndicator +
                            ", net=" + NetStatus + ", radar=" + RadarView + "/" + RadarProj + "/" + RadarAny + ", gpu=" + GpuUv + "/" + GpuType + "/" + GpuTarget + "/" + GpuSize +
                            ", binds=" + SeatList + "/" + BindTarget + "/" + BindDistance + "/" + BindSize + ", renderer draw0=" + RendDraw0 + ", bounds=" + BoundsAabb +
                            ", paint=" + PaintPainted + "/" + PaintDefault + ", camera=" + CamEntity + "/" + CamNextDraw + "/" + CamSlice + ", monitor=" + MonSignals + "/" + MonSlices +
                            ", player camera=" + PlayerCamera);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Offset of a field inside the struct (il2cpp counts the 16-byte object header, structs don't have it).</summary>
        public static int Off(Type t, string field)
        {
            var k = Il2CppClassPointerStore.GetNativeClassPointer(t);
            if (k == IntPtr.Zero) throw new InvalidOperationException(t.Name + " is not in the game");
            var f = IL2CPP.il2cpp_class_get_field_from_name(k, field);
            if (f == IntPtr.Zero) throw new InvalidOperationException(t.Name + "." + field + " is not in the game");
            int off = (int)IL2CPP.il2cpp_field_get_offset(f) - 16;
            if (off < 0) throw new InvalidOperationException(t.Name + "." + field + ": offset " + (off + 16) + " too small");
            return off;
        }

        /// <summary>Size of a struct in the game (an element's stride in a buffer or list).</summary>
        public static int Size(Type t)
        {
            uint align = 0;
            int n = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore.GetNativeClassPointer(t), ref align);
            if (n <= 0) throw new InvalidOperationException(t.Name + ": size " + n);
            return n;
        }
    }
}
