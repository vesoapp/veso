using Emby.Naming.Common;
using Emby.Naming.Video;

namespace veso.Naming.Tests.Video
{
    public abstract class BaseVideoTest
    {
        private readonly NamingOptions _namingOptions = new NamingOptions();

        protected VideoResolver GetParser()
            => new VideoResolver(_namingOptions);
    }
}
