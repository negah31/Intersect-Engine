using Intersect.Client.Core;
using Intersect.Client.Framework.Entities;
using Intersect.Client.Framework.GenericClasses;
using Intersect.Client.Framework.Graphics;
using Intersect.Client.Framework.Gwen.ControlInternal;
using Intersect.Client.General;
using Intersect.Framework.Core;
using Intersect.Utilities;

namespace Intersect.Client.Entities;

public partial class ChatBubble
{
    private readonly IGameTexture? mBubbleTex;

    private readonly Entity? mOwner;

    private readonly long mRenderTimer;

    private readonly string? mSourceText;

    private Point[,]? mTexSections;

    private string[]? mText;

    private Rectangle mTextBounds = new();

    private Rectangle mTextureBounds;

    public ChatBubble(Entity owner, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        mOwner = owner;
        mSourceText = text;
        mRenderTimer = Timing.Global.MillisecondsUtc + 5000;
        mBubbleTex = Globals.ContentManager.GetTexture(Framework.Content.TextureType.Misc, "chatbubble.png");
    }

    public bool Update()
    {
        if (mRenderTimer < Timing.Global.MillisecondsUtc)
        {
            return false;
        }

        return true;
    }

    public float Draw(float yoffset = 0f)
    {
        if (mText == null && mSourceText?.Trim().Length > 0 && Graphics.ChatBubbleFont != default)
        {
            mText = Text.WrapText(
                mSourceText,
                200,
                Graphics.ChatBubbleFont,
                Graphics.ChatBubbleFontSize,
                Graphics.Renderer ?? throw new InvalidOperationException("No renderer")
            );
        }

        if (mText == null || Graphics.Renderer == default || mOwner == default)
        {
            return 0f;
        }

        var x = (int)Math.Ceiling(mOwner.Origin.X);
        var y = (int)Math.Ceiling(mOwner.GetLabelLocation(LabelType.ChatBubble));

        if (mTextureBounds.Width == 0)
        {
            //Gotta Calculate Bounds
            for (var i = (mText?.Length ?? 0) - 1; i > -1; i--)
            {
                var textSize = Graphics.Renderer.MeasureText(
                    mText![i],
                    Graphics.ChatBubbleFont,
                    Graphics.ChatBubbleFontSize,
                    1
                );
                if (textSize.X > mTextureBounds.Width)
                {
                    mTextureBounds.Width = (int)textSize.X + 16;
                }

                mTextureBounds.Height += (int)textSize.Y + 2;
                if (textSize.X > mTextBounds.Width)
                {
                    mTextBounds.Width = (int)textSize.X;
                }

                mTextBounds.Height += (int)textSize.Y + 2;
            }

            mTextureBounds.Height += 16;
            if (mTextureBounds.Width < 48)
            {
                mTextureBounds.Width = 48;
            }

            if (mTextureBounds.Height < 32)
            {
                mTextureBounds.Height = 32;
            }

            mTextureBounds.Width = (int)(Math.Round(mTextureBounds.Width / 8.0) * 8.0);
            mTextureBounds.Height = (int)(Math.Round(mTextureBounds.Height / 8.0) * 8.0);
            if (mTextureBounds.Width / 8 % 2 != 0)
            {
                mTextureBounds.Width += 8;
            }

            mTexSections = new Point[mTextureBounds.Width / 8, mTextureBounds.Height / 8];
            for (var x1 = 0; x1 < mTextureBounds.Width / 8; x1++)
            {
                for (var y1 = 0; y1 < mTextureBounds.Height / 8; y1++)
                {
                    if (x1 == 0)
                    {
                        mTexSections[x1, y1].X = 0;
                    }
                    else if (x1 == 1)
                    {
                        mTexSections[x1, y1].X = 1;
                    }
                    else if (x1 == mTextureBounds.Width / 16 - 1)
                    {
                        mTexSections[x1, y1].X = 3;
                    }
                    else if (x1 == mTextureBounds.Width / 16)
                    {
                        mTexSections[x1, y1].X = 4;
                    }
                    else if (x1 == mTextureBounds.Width / 8 - 1)
                    {
                        mTexSections[x1, y1].X = 7;
                    }
                    else if (x1 == mTextureBounds.Width / 8 - 2)
                    {
                        mTexSections[x1, y1].X = 6;
                    }
                    else
                    {
                        mTexSections[x1, y1].X = 2;
                    }

                    if (y1 == 0)
                    {
                        mTexSections[x1, y1].Y = 0;
                    }
                    else if (y1 == 1)
                    {
                        mTexSections[x1, y1].Y = 1;
                    }
                    else if (y1 == mTextureBounds.Height / 8 - 1)
                    {
                        mTexSections[x1, y1].Y = 3;
                    }
                    else if (y1 == mTextureBounds.Height / 8 - 2)
                    {
                        mTexSections[x1, y1].Y = 2;
                    }
                    else
                    {
                        mTexSections[x1, y1].Y = 1;
                    }
                }
            }
        }

        if (mBubbleTex != null && mText != null && mTexSections != null)
        {
            //Draw Background if available
            //Draw Top Left
            for (var x1 = 0; x1 < mTextureBounds.Width / 8; x1++)
            {
                for (var y1 = 0; y1 < mTextureBounds.Height / 8; y1++)
                {
                    Graphics.Renderer.DrawTexture(
                        mBubbleTex,
                        mTexSections[x1, y1].X * 8,
                        mTexSections[x1, y1].Y * 8,
                        8,
                        8,
                        x - mTextureBounds.Width / 2 + x1 * 8,
                        y - mTextureBounds.Height - yoffset + y1 * 8,
                        8,
                        8,
                        Color.White
                    );
                }
            }

            for (var i = mText.Length - 1; i > -1; i--)
            {
                var textSize = Graphics.Renderer.MeasureText(
                    mText[i],
                    Graphics.ChatBubbleFont,
                    Graphics.ChatBubbleFontSize,
                    1
                );
                Graphics.Renderer.DrawString(
                    mText[i],
                    Graphics.ChatBubbleFont,
                    Graphics.ChatBubbleFontSize,
                    (int)(x - mTextureBounds.Width / 2 + (mTextureBounds.Width - textSize.X) / 2f),
                    (int)(y - mTextureBounds.Height - yoffset + 8 + i * 16),
                    1,
                    Color.FromArgb(CustomColors.Chat.ChatBubbleText.ToArgb()),
                    true,
                    null,
                    Color.FromArgb(CustomColors.Chat.ChatBubbleTextOutline.ToArgb())
                );
            }
        }

        yoffset += mTextureBounds.Height;

        return yoffset;
    }
}
