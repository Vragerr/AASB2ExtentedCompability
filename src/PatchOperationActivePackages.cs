using System.Collections.Generic;
using System.Xml;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class PatchOperationActivePackages : PatchOperation
    {
        private List<string> packages = null;
        private PatchOperation operation = null;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            if (packages == null || packages.Count == 0 || operation == null)
                return false;
            foreach (string package in packages)
                if (!ModsConfig.IsActive(package))
                    return true;
            return operation.Apply(xml);
        }
    }
}
