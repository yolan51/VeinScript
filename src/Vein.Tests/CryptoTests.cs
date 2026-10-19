using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// Vein.Crypto's primitives (VeinCrypto), below the language: what HostingTests' Vein_Crypto theory checks from
// a program, here part by part. Every function is total — a box that is not genuine opens to "".
public class CryptoTests
{
    [Fact]
    public void A_box_opens_for_its_recipient_from_its_sender_and_for_nobody_else()
    {
        string alice = VeinCrypto.NewKeys(), bob = VeinCrypto.NewKeys(), eve = VeinCrypto.NewKeys();
        string box = VeinCrypto.Seal("meet at noon", VeinCrypto.PublicKey(bob), alice);

        Assert.DoesNotContain("noon", box);
        Assert.Equal("meet at noon", VeinCrypto.Open(box, bob, VeinCrypto.PublicKey(alice)));
        // Eve holds neither key; and Bob cannot be told Eve wrote it.
        Assert.Equal("", VeinCrypto.Open(box, eve, VeinCrypto.PublicKey(alice)));
        Assert.Equal("", VeinCrypto.Open(box, bob, VeinCrypto.PublicKey(eve)));
    }

    [Fact]
    public void Changing_any_part_of_a_box_stops_it_opening()
    {
        string a = VeinCrypto.NewKeys(), b = VeinCrypto.NewKeys();
        string box = VeinCrypto.Seal("same", VeinCrypto.PublicKey(b), a);
        string[] parts = box.Split(':');
        for (int i = 1; i < parts.Length; i++)
        {
            byte[] bytes = Convert.FromBase64String(parts[i]);
            bytes[^1] ^= 1;
            string[] changed = (string[])parts.Clone();
            changed[i] = Convert.ToBase64String(bytes);
            Assert.Equal("", VeinCrypto.Open(string.Join(":", changed), b, VeinCrypto.PublicKey(a)));
        }
        Assert.NotEqual(box, VeinCrypto.Seal("same", VeinCrypto.PublicKey(b), a));
    }

    [Fact]
    public void Keys_and_boxes_are_text_a_line_protocol_can_carry()
    {
        string a = VeinCrypto.NewKeys(), b = VeinCrypto.NewKeys();
        string box = VeinCrypto.Seal("ünïcödé | and spaces", VeinCrypto.PublicKey(b), a);
        foreach (string s in new[] { a, VeinCrypto.PublicKey(a), box })
        {
            Assert.DoesNotContain(" ", s);
            Assert.DoesNotContain("|", s);
            Assert.DoesNotContain(";", s);
        }
        Assert.Equal("ünïcödé | and spaces", VeinCrypto.Open(box, b, VeinCrypto.PublicKey(a)));
    }

    [Fact]
    public void A_locked_text_opens_with_its_password_only()
    {
        string locked = VeinCrypto.Lock("my keys", "correct horse");
        Assert.Equal("my keys", VeinCrypto.Unlock(locked, "correct horse"));
        Assert.Equal("", VeinCrypto.Unlock(locked, "wrong horse"));
        Assert.Equal("", VeinCrypto.Unlock("nonsense", "correct horse"));
    }
}
