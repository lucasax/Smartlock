﻿using System;
using System.Collections;
using System.Threading;

using Microsoft.SPOT;

using GHI.Glide;
using GHI.Glide.Display;
using GT = Gadgeteer; 
using Gadgeteer.Modules.GHIElectronics;
using Gadgeteer.Modules.Luca_Sasselli;

namespace SmartLock
{
    public partial class Program
    {
        // Main Objects
        private DataHelper dataHelper;
        private Display display;

        public void ProgramStarted()
        {
            Debug.Print("Program started!");          

            // Object init
            dataHelper = new DataHelper(ethernetJ11D, sdCard);
            display = new Display();

            // NFC Setup
            adafruit_PN532.TagFound += TagFound;
            //adafruit_PN532.StartScan(1000, 100);

            // Pin Setup
            display.PinFound += PinFound;
        }

        /*
         * This event occurs when the user passes a NFC card near the reader.
         * It checks if the UID is valid and unlock the door if so.
         */
        public void TagFound(string uid)
        {
            // Check authorization
            bool authorized = dataHelper.CheckCardID(uid);

            // Show access window
            display.ShowAccessWindow(authorized);

            // Log the event
            Log accessLog; //create a new log
            string logText;
            if (authorized)
            {
                // Access granted
                UnlockDoor();
                logText = "Card " + uid + " inserted. Authorized access.";
            }
            else
            {
                // Access denied
                logText = "Card " + uid + " inserted. Access denied!";
            }
            Debug.Print(logText);
            accessLog = new Log(2, logText, DateTime.Now.ToString());
            dataHelper.AddLog(accessLog); //add log to log list
        }

        /*
         * This event occurs when the user inserts a pin code.
         * It checks if the pin is valid and unlock the door if so.
         */
        public void PinFound(string pin)
        {
            // Check authorization
            bool authorized = dataHelper.CheckPin(pin);

            // Show access window
            display.ShowAccessWindow(authorized);

            // Log the event
            Log accessLog; //create a new log
            string logText;
            if (authorized)
            {
                // Access granted
                UnlockDoor();
                logText = "Pin " + pin + " inserted. Authorized access.";
            }
            else
            {
                // Access denied
                logText = "Pin " + pin + " inserted. Access denied!";
            }
            Debug.Print(logText);
            accessLog = new Log(2, logText, DateTime.Now.ToString());
            dataHelper.AddLog(accessLog); //add log to log list

            // TODO: if UID is null ask to add card
        }

        /*
         * Called by either PinFound or TagFound to unlock the door.
         */
        private void UnlockDoor()
        {
            //TODO
        }
    }
}


