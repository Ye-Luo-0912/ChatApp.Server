namespace Core.Models
{
    /// <summary>
    /// 定义了电子邮件验证码的不同用途。
    /// </summary>
    public enum EmailCodePurpose
    {

        /// <summary>
        /// 注册
        /// </summary>
        Register = 1,

        /// <summary>
        /// 重置密码
        /// </summary>
        ResetPassword = 2,

        /// <summary>
        /// 用于更改邮箱时的验证码目的。
        /// </summary>
        ChangeEmail = 3,

        /// <summary>
        /// 绑定邮箱
        /// </summary>
        BindEmail = 4
    }
}
