using System.Collections.Generic;

namespace CicodeSim.World.Display
{
    public sealed partial class PageInstance
    {
        /// <summary>Bumped by every change of the object set or of a parent (Ensure, Take, an
        /// object freed): the children index below is rebuilt after one.</summary>
        private int _structure;
        private int _childrenAt = -1;
        private Dictionary<int, List<AnObject>>? _children;
        private static readonly List<AnObject> NoChildren = new List<AnObject>();

        /// <summary>The object set or a parent changed (the children index is stale).</summary>
        public void StructureChanged() => _structure++;

        /// <summary>The children of ``parent`` in page order.</summary>
        public List<AnObject> ChildList(int parent)
        {
            if (_children == null || _childrenAt != _structure)
            {
                var idx = new Dictionary<int, List<AnObject>>();
                foreach (int an in Order)
                {
                    if (!Objects.TryGetValue(an, out var o))
                        continue;
                    if (!idx.TryGetValue(o.Parent, out var l))
                        idx[o.Parent] = l = new List<AnObject>();
                    l.Add(o);
                }
                _children = idx;
                _childrenAt = _structure;
            }
            return _children.TryGetValue(parent, out var r) ? r : NoChildren;
        }
    }
}
