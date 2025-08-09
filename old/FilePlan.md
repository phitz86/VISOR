GRiD 
├── Program.cs
└── GRiD-C.csproj 
└── IracingTelemetryWrapper.cs  
│
├── Wrappers/
│   ├── IRSDKSharperWrapper.cs
│   ├── IRSDKSharperSnapshot.cs
│   ├── VipooSDKWrapper.cs
│   └── VipooSnapshot.cs
│
├── Services/
│   ├── TelemetryInspectorService.cs
│   └── PerformanceMonitor.cs
│
├── Logging/
│   └── TelemetrySnapshotLogger.cs
│
├── Models/
│   └── TelemetrySnapshotJson.cs   // Output schema representation
│
├── Diagnostics/
│   └── WrapperDiffEngine.cs       // Optional diff report generator
│
├── Utilities/
│   └── TelemetryFieldHelper.cs    // (if we want to consolidate known field names/types)
