namespace DesktopIconsToggle.Tests;

// The test host has no package identity: these run the real WinRT path up to the point where
// Windows refuses it, which must fail closed instead of throwing or hanging.
public class StartupTaskAutostartTests
{
    [Fact]
    public void Test_host_is_not_packaged()
    {
        Assert.False(StartupTaskAutostart.IsPackaged());
    }

    [Fact]
    public void Without_package_identity_reports_disabled()
    {
        Assert.False(new StartupTaskAutostart().IsEnabled());
    }

    [Fact]
    public void Without_package_identity_toggle_changes_nothing()
    {
        Assert.False(new StartupTaskAutostart().Toggle());
    }
}
