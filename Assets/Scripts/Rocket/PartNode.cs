using System.Collections.Generic;

// A node in an assembly tree. The root holds a base module; children are modules.
public class PartNode
{
    public readonly ModuleData data;
    public readonly PartNode parent;
    public readonly List<PartNode> children = new List<PartNode>();

    public PartNode(ModuleData data, PartNode parent)
    {
        this.data = data;
        this.parent = parent;
    }

    public PartNode AddChild(ModuleData module)
    {
        var node = new PartNode(module, this);
        children.Add(node);
        return node;
    }

    // Removes this node and its subtree. The root has no parent and cannot be removed.
    public void Remove() => parent?.children.Remove(this);

    // Depth-first, parent before children: base module first, then what sits above it.
    public IEnumerable<PartNode> Walk()
    {
        yield return this;
        foreach (var c in children)
            foreach (var n in c.Walk()) yield return n;
    }
}
