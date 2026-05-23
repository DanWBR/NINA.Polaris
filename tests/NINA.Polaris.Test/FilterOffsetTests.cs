using NINA.Polaris.Services;
using NINA.Polaris.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FilterOffsetTests {
    [Test]
    public void EquipmentProfile_FilterOffsets_DefaultsEmpty() {
        var p = new EquipmentProfile();
        Assert.That(p.FilterOffsets, Is.Not.Null);
        Assert.That(p.FilterOffsets.Count, Is.EqualTo(0));
    }

    [Test]
    public void EquipmentProfile_FilterOffsets_Roundtrips() {
        var p = new EquipmentProfile();
        p.FilterOffsets["L"] = 0;
        p.FilterOffsets["R"] = -12;
        p.FilterOffsets["G"] = -8;
        Assert.That(p.FilterOffsets["L"], Is.EqualTo(0));
        Assert.That(p.FilterOffsets["R"], Is.EqualTo(-12));
        Assert.That(p.FilterOffsets["G"], Is.EqualTo(-8));
    }

    [Test]
    public void MoveToFilterOffsetInstruction_TypeDiscriminator_Stable() {
        var i = new MoveToFilterOffsetInstruction { FilterName = "R", OffsetSteps = -12 };
        Assert.That(i.Type, Is.EqualTo("MoveToFilterOffset"));
    }
}
