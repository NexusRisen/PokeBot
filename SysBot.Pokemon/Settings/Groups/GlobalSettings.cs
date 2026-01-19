using System.ComponentModel;
using System.Linq;

namespace SysBot.Pokemon;

public class GlobalSettings : ICustomTypeDescriptor
{
    private const string FeatureToggle = nameof(FeatureToggle);
    private const string Operation = nameof(Operation);
    private const string BotTrade = nameof(BotTrade);
    private const string Integration = nameof(Integration);

    [Browsable(false)]
    public ProgramMode CurrentMode { get; set; } = ProgramMode.None;

    public bool ShouldSerializeAntiIdle() => CurrentMode != ProgramMode.SV;

    [Category(BotTrade), Description("Name of the Discord Bot the Program is Running. This will Title the window for easier recognition. Requires program restart.")]
    public string BotName { get; set; } = string.Empty;

    [Browsable(false)]
    [Category(Integration), Description("Users Theme Option Choice.")]
    public string ThemeOption { get; set; } = string.Empty;

    [Category(FeatureToggle), Description("When enabled, the bot will press the B button occasionally when it is not processing anything (to avoid sleep).")]
    public bool AntiIdle { get; set; }

    [Category(FeatureToggle), Description("Enables text logs. Restart to apply changes.")]
    public bool LoggingEnabled { get; set; } = true;

    [Category(FeatureToggle), Description("Maximum number of old text log files to retain. Set this to <= 0 to disable log cleanup. Restart to apply changes.")]
    public int MaxArchiveFiles { get; set; } = 14;

    [Category(Operation)]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public FolderSettings Folder { get; set; } = new();

    [Category(Operation)]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public LegalitySettings Legality { get; set; } = new();

    [Category(Operation), Description("Settings for automatic bot recovery after crashes.")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public RecoverySettings Recovery { get; set; } = new();

    [Category(Operation), Description("Add extra time for slower Switches.")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public TimingSettings Timings { get; set; } = new();

    [Browsable(false)]
    [Category("Debug"), Description("Skips creating bots when the program is started; helpful for testing integrations.")]
    public bool SkipConsoleBotCreation { get; set; }

    public override string ToString() => "Global Settings";

    // ICustomTypeDescriptor implementation
    public AttributeCollection GetAttributes() => TypeDescriptor.GetAttributes(this, true);
    public string? GetClassName() => TypeDescriptor.GetClassName(this, true);
    public string? GetComponentName() => TypeDescriptor.GetComponentName(this, true);
    public TypeConverter? GetConverter() => TypeDescriptor.GetConverter(this, true);
    public EventDescriptor? GetDefaultEvent() => TypeDescriptor.GetDefaultEvent(this, true);
    public PropertyDescriptor? GetDefaultProperty() => TypeDescriptor.GetDefaultProperty(this, true);
    public object? GetEditor(Type editorBaseType) => TypeDescriptor.GetEditor(this, editorBaseType, true);
    public EventDescriptorCollection GetEvents() => TypeDescriptor.GetEvents(this, true);
    public EventDescriptorCollection GetEvents(Attribute[]? attributes) => TypeDescriptor.GetEvents(this, attributes, true);

    public PropertyDescriptorCollection GetProperties() => GetProperties(null);

    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
    {
        var properties = TypeDescriptor.GetProperties(this, attributes, true);
        var filtered = properties.Cast<PropertyDescriptor>().Where(prop =>
        {
            if (prop.Name == nameof(AntiIdle) && CurrentMode == ProgramMode.SV)
                return false;
            return true;
        }).ToArray();
        return new PropertyDescriptorCollection(filtered);
    }
}
