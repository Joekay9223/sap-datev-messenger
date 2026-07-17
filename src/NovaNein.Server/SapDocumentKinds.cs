using System;
using NovaNein.Domain;

namespace NovaNein.Server;

public static class SapDocumentKinds
{
	public static SapBusinessDocumentType ToDomain(this SapDocumentKind kind)
	{
		return kind switch
		{
			SapDocumentKind.PurchaseInvoice => SapBusinessDocumentType.PurchaseInvoice,
			SapDocumentKind.Invoice => SapBusinessDocumentType.Invoice,
			SapDocumentKind.PurchaseCreditNote => SapBusinessDocumentType.PurchaseCreditNote,
			SapDocumentKind.CreditNote => SapBusinessDocumentType.CreditNote,
			_ => throw new ArgumentOutOfRangeException("kind"),
		};
	}

	public static SapDocumentKind ToServer(this SapBusinessDocumentType kind, DocumentDirection direction)
	{
		switch (kind)
		{
		case SapBusinessDocumentType.PurchaseInvoice:
			return SapDocumentKind.PurchaseInvoice;
		case SapBusinessDocumentType.Invoice:
			return SapDocumentKind.Invoice;
		case SapBusinessDocumentType.PurchaseCreditNote:
			return SapDocumentKind.PurchaseCreditNote;
		case SapBusinessDocumentType.CreditNote:
			return SapDocumentKind.CreditNote;
		case SapBusinessDocumentType.Unspecified:
			if (direction == DocumentDirection.Incoming)
			{
				return SapDocumentKind.PurchaseInvoice;
			}
			return SapDocumentKind.Invoice;
		default:
			throw new ArgumentOutOfRangeException("kind");
		}
	}

	public static bool IsCreditNote(this SapBusinessDocumentType kind)
	{
		if ((uint)(kind - 3) <= 1u)
		{
			return true;
		}
		return false;
	}

	public static bool IsInvoice(this SapBusinessDocumentType kind)
	{
		if ((uint)kind <= 2u)
		{
			return true;
		}
		return false;
	}

	public static bool IsCreditNote(this SapDocumentKind kind)
	{
		return kind is SapDocumentKind.PurchaseCreditNote or SapDocumentKind.CreditNote;
	}
}
