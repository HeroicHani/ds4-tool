﻿using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles; 
namespace HidLibrary
{
    public class HidDevice : IDisposable
    {
        public event InsertedEventHandler Inserted;
        public event RemovedEventHandler Removed;

        public event EventHandler<EventArgs> Insert;
        public event EventHandler<EventArgs> Remove;

        public delegate void InsertedEventHandler();
        public delegate void RemovedEventHandler();

        public enum ReadStatus
        {
            Success = 0,
            WaitTimedOut = 1,
            WaitFail = 2,
            NoDataRead = 3,
            ReadError = 4,
            NotConnected = 5
        }

        private readonly string _description;
        private readonly string _devicePath;
        private readonly HidDeviceAttributes _deviceAttributes;

        private readonly HidDeviceCapabilities _deviceCapabilities;
        private readonly HidDeviceEventMonitor _deviceEventMonitor;
        private byte idleTicks = 0;
        private bool _monitorDeviceEvents;

        internal HidDevice(string devicePath, string description = null)
        {
            _deviceEventMonitor = new HidDeviceEventMonitor(this);
            _deviceEventMonitor.Inserted += DeviceEventMonitorInserted;
            _deviceEventMonitor.Removed += DeviceEventMonitorRemoved;

            _devicePath = devicePath;
            _description = description;

            try
            {
                var hidHandle = OpenHandle(_devicePath, false);

                _deviceAttributes = GetDeviceAttributes(hidHandle);
                _deviceCapabilities = GetDeviceCapabilities(hidHandle);

                hidHandle.Close();
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception.Message);
                throw new Exception(string.Format("Error querying HID device '{0}'.", devicePath), exception);
            }
        }

        public SafeFileHandle safeReadHandle { get; private set; }
        public FileStream fileStream { get; private set; }
        public bool IsOpen { get; private set; }
        public bool IsConnected { get { return HidDevices.IsConnected(_devicePath) && idleTicks<=5; } }
        public bool IsTimedOut { get { return idleTicks > 5; } }
        public string Description { get { return _description; } }
        public HidDeviceCapabilities Capabilities { get { return _deviceCapabilities; } }
        public HidDeviceAttributes Attributes { get { return _deviceAttributes; } }
        public string DevicePath { get { return _devicePath; } }

        public bool MonitorDeviceEvents
        {
            get { return _monitorDeviceEvents; }
            set
            {
                if (value & _monitorDeviceEvents == false) _deviceEventMonitor.Init();
                _monitorDeviceEvents = value;
            }
        }

        public override string ToString()
        {
            return string.Format("VendorID={0}, ProductID={1}, Version={2}, DevicePath={3}", 
                                _deviceAttributes.VendorHexId,
                                _deviceAttributes.ProductHexId,
                                _deviceAttributes.Version,
                                _devicePath);
        }

        public void OpenDevice(bool isExclusive)
        {
            if (IsOpen) return;
            try
            {
                if (safeReadHandle == null)
                    safeReadHandle = OpenHandle(_devicePath, isExclusive);
            }
            catch (Exception exception)
            {
                IsOpen = false;
                throw new Exception("Error opening HID device.", exception);
            }

            IsOpen = !safeReadHandle.IsInvalid;
        }

        public void CloseDevice()
        {
            if (!IsOpen) return;
            closeFileStreamIO();

            IsOpen = false;
        }

        public bool ReadInputReport(byte[] data)
        {
            if (safeReadHandle == null)
                safeReadHandle = OpenHandle(_devicePath, true);
            return NativeMethods.HidD_GetInputReport(safeReadHandle, data, data.Length);
        }


        private static HidDeviceAttributes GetDeviceAttributes(SafeFileHandle hidHandle)
        {
            var deviceAttributes = default(NativeMethods.HIDD_ATTRIBUTES);
            deviceAttributes.Size = Marshal.SizeOf(deviceAttributes);
            NativeMethods.HidD_GetAttributes(hidHandle.DangerousGetHandle(), ref deviceAttributes);
            return new HidDeviceAttributes(deviceAttributes);
        }

        private static HidDeviceCapabilities GetDeviceCapabilities(SafeFileHandle hidHandle)
        {
            var capabilities = default(NativeMethods.HIDP_CAPS);
            var preparsedDataPointer = default(IntPtr);

            if (NativeMethods.HidD_GetPreparsedData(hidHandle.DangerousGetHandle(), ref preparsedDataPointer))
            {
                NativeMethods.HidP_GetCaps(preparsedDataPointer, ref capabilities);
                NativeMethods.HidD_FreePreparsedData(preparsedDataPointer);
            }
            return new HidDeviceCapabilities(capabilities);
        }

        private void closeFileStreamIO()
        {
            if(fileStream!=null)
                fileStream.Close();
            fileStream = null;
            Console.WriteLine("Close fs");
            if (safeReadHandle!=null && !safeReadHandle.IsInvalid)
            {
                safeReadHandle.Close();
                Console.WriteLine("Close sh");

            }
            safeReadHandle = null;
        }

        private void DeviceEventMonitorInserted()
        {
            if (IsOpen) OpenDevice(false);
            if (Inserted != null) Inserted();
        }

        private void DeviceEventMonitorRemoved()
        {
            if (IsOpen)
            {
                MonitorDeviceEvents = false;
                Console.WriteLine("Cancelling IO");
                NativeMethods.CancelIoEx(safeReadHandle.DangerousGetHandle(), IntPtr.Zero);
                Console.WriteLine("Cancelled IO");
                CloseDevice();
                idleTicks = 6; // force timeOut on USB
                Console.WriteLine("Device is closed.");
            }
            if (Removed != null) Removed();
            if (Remove != null) Remove(this, new EventArgs());
        }

        public void Tick()
        {
            idleTicks++;
            Console.WriteLine(idleTicks);
        }

        public void Dispose()
        {
            if (MonitorDeviceEvents) MonitorDeviceEvents = false;
            if (IsOpen) CloseDevice();
        }
        public void flush_Queue()
        {
            if (safeReadHandle != null)
            {
                NativeMethods.HidD_FlushQueue(safeReadHandle);
            }
        }

        private ReadStatus ReadWithFileStreamTask(byte[] inputBuffer)
        {
            try
            {
                if (fileStream.Read(inputBuffer, 0, inputBuffer.Length) > 0)
                {
                    return ReadStatus.Success;
                }
                else
                {
                    return ReadStatus.NoDataRead;
                }
            }
            catch (Exception)
            {
                return ReadStatus.ReadError;
            }
        }
        public ReadStatus ReadFile(byte[] inputBuffer)
        {
            if (safeReadHandle == null)
                safeReadHandle = OpenHandle(_devicePath, true);
            try
            {
                uint bytesRead;
                idleTicks = 0;
                if (NativeMethods.ReadFile(safeReadHandle.DangerousGetHandle(), inputBuffer, (uint)inputBuffer.Length, out bytesRead, IntPtr.Zero))
                {
                    return ReadStatus.Success;
                }
                else
                {
                    return ReadStatus.NoDataRead;
                }
            }
            catch (Exception)
            {
                return ReadStatus.ReadError;
            }
                            


            
        }

        public ReadStatus ReadWithFileStream(byte[] inputBuffer, int timeout)
        {
               try
                {
                    if (safeReadHandle == null)
                        safeReadHandle = OpenHandle(_devicePath, true);
                    if (fileStream == null && !safeReadHandle.IsInvalid)
                        fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, inputBuffer.Length, false);
                    if (!safeReadHandle.IsInvalid && fileStream.CanRead)
                    {

                        Task<ReadStatus> readFileTask = new Task<ReadStatus>(() => ReadWithFileStreamTask(inputBuffer));
                        readFileTask.Start();
                        bool success = readFileTask.Wait(timeout);
                        if (success)
                        {
                            if (readFileTask.Result == ReadStatus.Success)
                            {
                                return ReadStatus.Success;
                            }
                            else if (readFileTask.Result == ReadStatus.ReadError)
                            {
                                return ReadStatus.ReadError;
                            }
                            else if (readFileTask.Result == ReadStatus.NoDataRead)
                            {
                                return ReadStatus.NoDataRead;
                            }
                        }
                        else
                            return ReadStatus.WaitTimedOut;
                    }
                  
                }
                catch (Exception e)
                {
                    if (e is AggregateException)
                    {
                        Console.WriteLine(e.Message);
                        return ReadStatus.WaitFail;
                    }
                    else {
                        return ReadStatus.ReadError;
                    }
                }

            
                
            
            return ReadStatus.ReadError;
        }

        public bool WriteOutputReportViaControl(byte[] outputBuffer)
        {
            if (safeReadHandle == null)
            {
                safeReadHandle = OpenHandle(_devicePath, true);
            }

            if (NativeMethods.HidD_SetOutputReport(safeReadHandle, outputBuffer, outputBuffer.Length))
                return true;
            else
                return false;
        }

        private bool WriteOutputReportViaInterruptTask(byte[] outputBuffer)
        {
            try
            {
                fileStream.Write(outputBuffer, 0, outputBuffer.Length);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public bool WriteOutputReportViaInterrupt(byte[] outputBuffer, int timeout)
        {
            try
            {
                if (safeReadHandle == null)
                {
                    safeReadHandle = OpenHandle(_devicePath, true);
                }
                if (fileStream == null && !safeReadHandle.IsInvalid)
                {
                    fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, outputBuffer.Length, false);
                }
                if (fileStream != null && fileStream.CanWrite && !safeReadHandle.IsInvalid)
                {
                    fileStream.Write(outputBuffer, 0, outputBuffer.Length);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                return false;
            }

        }

        private SafeFileHandle OpenHandle(String devicePathName, Boolean isExclusive)
        {
            SafeFileHandle hidHandle;

            try
            {
                if (isExclusive)
                {
                    hidHandle = NativeMethods.CreateFile(devicePathName, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OpenExisting, 0, 0);
                }
                else
                {
                    hidHandle = NativeMethods.CreateFile(devicePathName, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OpenExisting, 0, 0);
                }
            }
            catch (Exception)
            {
                throw;
            }
            return hidHandle;
        }

        public bool readFeatureData(byte[] inputBuffer )
        {
            return NativeMethods.HidD_GetFeature(safeReadHandle.DangerousGetHandle(), inputBuffer, inputBuffer.Length);
        }
    }
}
