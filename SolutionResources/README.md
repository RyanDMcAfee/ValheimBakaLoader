# Solution Resources

This folder contains code, configuration, and/or assets that are used in multiple projects in the Solution. Some files are considered "secret" and are not committed to source control. However, the solution is set up so that you **should not need any of these secret files** in order to do local development - only to publish a signed release of the app.

In some cases, however, you may want to supply your own mock secret values for testing. Examples of these files are provided below for your reference.

### ValheimBakaLoader.snk

This is only needed when publishing the desktop client application in the Release configuration. If you need to publish the application locally for some reason, simply change the Publish Profile (.pubxml) to publish to Debug configuration temporarily.

### ClientSecrets.Values.cs

A way of providing secret information to the client application at compile time. Use this partial static class to set the values of any properties in **ClientSecrets.cs**.

```csharp
namespace ValheimBakaLoader.Properties
{
  public static partial class ClientSecrets
  {
    static ClientSecrets()
    {
      // Set the values of any properties in ClientSecrets.cs below
      RemoteApiKeyHeader = "some-header-key";
    }
  }
}
```
