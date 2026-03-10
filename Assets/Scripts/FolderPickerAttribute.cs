using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class FolderPickerAttribute : PropertyAttribute
{
    // 可选约束：目录内必须存在此相对路径文件，否则不给选中。
    public string hasToContainFile { get; private set; }

    public FolderPickerAttribute(string hasToContainFile = null)
    {
        this.hasToContainFile = hasToContainFile;
    }
}
